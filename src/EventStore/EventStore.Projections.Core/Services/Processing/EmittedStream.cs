// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Text;
using EventStore.Common.Log;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Projections.Core.Messages;

namespace EventStore.Projections.Core.Services.Processing
{
    public class EmittedStream : IDisposable, IHandle<CoreProjectionProcessingMessage.EmittedStreamWriteCompleted>
    {
        private readonly
            RequestResponseDispatcher<ClientMessage.ReadStreamEventsBackward, ClientMessage.ReadStreamEventsBackwardCompleted>
            _readDispatcher;

        private readonly RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted>
            _writeDispatcher;


        private readonly ILogger _logger;
        private readonly string _streamId;
        private readonly ProjectionVersion _projectionVersion;
        private readonly CheckpointTag _zeroPosition;
        private readonly CheckpointTag _from;
        private readonly IEmittedStreamContainer _readyHandler;

        private readonly Stack<Tuple<CheckpointTag, string, int>> _alreadyCommittedEvents = new Stack<Tuple<CheckpointTag, string, int>>();
        private readonly Queue<EmittedEvent> _pendingWrites = new Queue<EmittedEvent>();

        private bool _checkpointRequested;
        private bool _awaitingWriteCompleted;
        private bool _awaitingReady;
        private bool _awaitingListEventsCompleted;
        private bool _started;

        private readonly int _maxWriteBatchLength;
        private CheckpointTag _lastSubmittedOrCommittedMetadata; // TODO: rename
        private Event[] _submittedToWriteEvents;
        private EmittedEvent[] _submittedToWriteEmittedEvents;
        private int _lastKnownEventNumber = ExpectedVersion.Invalid;
        private readonly bool _noCheckpoints;
        private bool _disposed;
        private CheckpointTag _last;
        private bool _recoveryCompleted;


        public EmittedStream(
            string streamId, ProjectionVersion projectionVersion, CheckpointTag zeroPosition, CheckpointTag from,
            RequestResponseDispatcher
                <ClientMessage.ReadStreamEventsBackward, ClientMessage.ReadStreamEventsBackwardCompleted> readDispatcher,
            RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted> writeDispatcher,
            IEmittedStreamContainer readyHandler, int maxWriteBatchLength, ILogger logger = null,
            bool noCheckpoints = false)
        {
            if (streamId == null) throw new ArgumentNullException("streamId");
            if (zeroPosition == null) throw new ArgumentNullException("zeroPosition");
            if (@from == null) throw new ArgumentNullException("from");
            if (readDispatcher == null) throw new ArgumentNullException("readDispatcher");
            if (writeDispatcher == null) throw new ArgumentNullException("writeDispatcher");
            if (readyHandler == null) throw new ArgumentNullException("readyHandler");
            if (streamId == "") throw new ArgumentException("streamId");
            _streamId = streamId;
            _projectionVersion = projectionVersion;
            _zeroPosition = zeroPosition;
            _from = @from;
            _last = null;
            _readDispatcher = readDispatcher;
            _writeDispatcher = writeDispatcher;
            _readyHandler = readyHandler;
            _maxWriteBatchLength = maxWriteBatchLength;
            _logger = logger;
            _noCheckpoints = noCheckpoints;
        }

        public void EmitEvents(EmittedEvent[] events)
        {
            if (events == null) throw new ArgumentNullException("events");
            CheckpointTag groupCausedBy = null;
            foreach (var @event in events)
            {
                if (groupCausedBy == null)
                {
                    groupCausedBy = @event.CausedByTag;
                    if (!(_last != null && groupCausedBy > _last) && !(_last == null && groupCausedBy >= _from))
                        throw new InvalidOperationException(
                            string.Format("Invalid event order.  '{0}' goes after '{1}'", @event.CausedByTag, _last));
                    _last = groupCausedBy;
                }
                else if (@event.CausedByTag != groupCausedBy)
                    throw new ArgumentException("events must share the same CausedByTag");
                if (@event.StreamId != _streamId)
                    throw new ArgumentException("Invalid streamId", "events");
            }
            EnsureCheckpointNotRequested();
            foreach (var @event in events)
                _pendingWrites.Enqueue(@event);
            ProcessWrites();
        }

        public void Checkpoint()
        {
            EnsureCheckpointsEnabled();
            EnsureStreamStarted();
            EnsureCheckpointNotRequested();
            _checkpointRequested = true;
            ProcessRequestedCheckpoint();
        }

        public void Start()
        {
            EnsureCheckpointNotRequested();
            if (_started)
                throw new InvalidOperationException("Stream is already started");
            _started = true;
            ProcessWrites();
        }

        public int GetWritePendingEvents()
        {
            return _pendingWrites.Count;
        }

        public int GetWritesInProgress()
        {
            return _awaitingWriteCompleted ? 1 : 0;
        }

        public int GetReadsInProgress()
        {
            return _awaitingListEventsCompleted ? 1 : 0;
        }

        public void Handle(ClientMessage.WriteEventsCompleted message)
        {
            if (!_awaitingWriteCompleted)
                throw new InvalidOperationException("WriteEvents has not been submitted");
            if (_disposed)
                return;
            if (message.Result == OperationResult.Success)
            {
                var firstEventNumber = message.FirstEventNumber + (_lastKnownEventNumber == ExpectedVersion.NoStream ? 1 : 0); // account for stream crated
                _lastKnownEventNumber = firstEventNumber + _submittedToWriteEvents.Length - 1;
                NotifyEventsCommitted(_submittedToWriteEmittedEvents, firstEventNumber);
                OnWriteCompleted();
                return;
            }
            if (_logger != null)
            {
                _logger.Info("Failed to write events to stream {0}. Error: {1}",
                             _streamId,
                             Enum.GetName(typeof (OperationResult), message.Result));
            }
            switch (message.Result)
            {
                case OperationResult.WrongExpectedVersion:
                    RequestRestart(string.Format("The '{0}' stream has be written to from the outside", _streamId));
                    break;
                case OperationResult.PrepareTimeout:
                case OperationResult.ForwardTimeout:
                case OperationResult.CommitTimeout:
                    if (_logger != null) _logger.Info("Retrying write to {0}", _streamId);
                    PublishWriteEvents();
                    break;
                default:
                    throw new NotSupportedException("Unsupported error code received");
            }
        }

        private void RequestRestart(string reason)
        {
            _readyHandler.Handle(new CoreProjectionProcessingMessage.RestartRequested(Guid.Empty, reason));
        }

        private void Failed(string reason)
        {
            _readyHandler.Handle(new CoreProjectionProcessingMessage.Failed(Guid.Empty, reason));
        }

        private void ReadStreamEventsBackwardCompleted(ClientMessage.ReadStreamEventsBackwardCompleted message, CheckpointTag upTo)
        {
            if (upTo == _zeroPosition)
                throw new ArgumentException("upTo cannot be equal to zero position");

            if (!_awaitingListEventsCompleted)
                throw new InvalidOperationException("ReadStreamEventsBackward has not been requested");
            if (_disposed)
                return;
            _awaitingListEventsCompleted = false;

            var newPhysicalStream = message.Events.Length == 0;

            if (_lastSubmittedOrCommittedMetadata == null)
            {
                var parsed = default(CheckpointTagVersion);
                if (!newPhysicalStream)
                {
                    parsed = message.Events[0].Event.Metadata.ParseCheckpointTagJson(_projectionVersion);
                    if (_projectionVersion.ProjectionId != parsed.Version.ProjectionId)
                    {
                        Failed(
                            string.Format(
                                "Multiple projections emitting to the same stream detected.  Stream: '{0}'. Last event projection: '{1}'.  Emitting projection: '{2}'",
                                _streamId, parsed.Version.ProjectionId, _projectionVersion.ProjectionId));
                        return;
                    }
                }
                var newLogicalStream = newPhysicalStream
                    || (_projectionVersion.ProjectionId != parsed.Version.ProjectionId || _projectionVersion.Epoch > parsed.Version.Version);

                _lastKnownEventNumber = newPhysicalStream ? ExpectedVersion.NoStream : message.Events[0].Event.EventNumber;

                //TODO: throw exception when _projectionVersion.ProjectionId != parsed.ProjectionId ?

                if (newLogicalStream)
                    _lastSubmittedOrCommittedMetadata = _zeroPosition;
                else
                {
                    //TODO: verify order - as we are reading backward
                    _lastSubmittedOrCommittedMetadata = parsed.Tag;
                }
            }

            var stop = CollectAlreadyCommittedEvents(message, upTo);

            if (stop)
                SubmitWriteEventsInRecovery();
            else
                SubmitListEvents(upTo, message.NextEventNumber);

        }

        private bool CollectAlreadyCommittedEvents(ClientMessage.ReadStreamEventsBackwardCompleted message, CheckpointTag upTo)
        {
            var stop = false;
            foreach (var e in message.Events)
            {
                var checkpointTagVersion = e.Event.Metadata.ParseCheckpointTagJson(_projectionVersion);
                var ourEpoch = checkpointTagVersion.Version.ProjectionId == _projectionVersion.ProjectionId
                               && checkpointTagVersion.Version.Version >= _projectionVersion.Epoch;
                if (!ourEpoch || // ignore any events from previous projection epoch
                    checkpointTagVersion.Tag < upTo)
                    // ignore any events prior to the requested upTo (== first emitted event position)
                {
                    stop = true;
                    break;
                }
                var eventType = e.Event.EventType;
                _alreadyCommittedEvents.Push(Tuple.Create(checkpointTagVersion.Tag, eventType, e.Event.EventNumber));
            }
            return stop || message.IsEndOfStream;
        }

        private void ProcessWrites()
        {
            if (_started && !_awaitingWriteCompleted && _pendingWrites.Count > 0)
            {
                _awaitingWriteCompleted = true;
                var firstEvent = _pendingWrites.Peek();
                if (_lastSubmittedOrCommittedMetadata == null)
                    SubmitListEvents(firstEvent.CausedByTag);
                else
                    SubmitWriteEventsInRecovery();
            }
        }

        private void SubmitListEvents(CheckpointTag upTo, int fromEventNumber = -1)
        {
            _awaitingListEventsCompleted = true;
            _readDispatcher.Publish(
                new ClientMessage.ReadStreamEventsBackward(
                    //TODO: reading events history in batches of 1 event (slow?)
                    Guid.NewGuid(), _readDispatcher.Envelope, _streamId, fromEventNumber, 1, resolveLinks: false, validationStreamVersion: null), 
                        completed => ReadStreamEventsBackwardCompleted(completed, upTo));
        }

        private void SubmitWriteEvents()
        {
            var events = new List<Event>();
            var emittedEvents = new List<EmittedEvent>();
            while (_pendingWrites.Count > 0 && events.Count < _maxWriteBatchLength)
            {
                var e = _pendingWrites.Peek();
                if (!e.IsReady())
                {
                    _readyHandler.Handle(new CoreProjectionProcessingMessage.EmittedStreamAwaiting(_streamId, new SendToThisEnvelope(this)));
                    _awaitingReady = true;
                    break;
                }
                _pendingWrites.Dequeue();

                var expectedTag = e.ExpectedTag;
                var causedByTag = e.CausedByTag;
                if (expectedTag != null)
                    if (DetectConcurrencyViolations(expectedTag))
                    {
                        RequestRestart(
                            string.Format(
                                "Wrong expected tag while submitting write event request to the '{0}' stream.  The last known stream tag is: '{1}'  the expected tag is: '{2}'",
                                _streamId, _lastSubmittedOrCommittedMetadata, expectedTag));
                        return;
                    }
                _lastSubmittedOrCommittedMetadata = causedByTag;
                events.Add(
                    new Event(
                        e.EventId, e.EventType, true, e.Data != null ? Encoding.UTF8.GetBytes(e.Data) : null,
                        e.CausedByTag.ToJsonBytes(_projectionVersion, e.ExtraMetaData())));
                emittedEvents.Add(e);
            }
            _submittedToWriteEvents = events.ToArray();
            _submittedToWriteEmittedEvents = emittedEvents.ToArray();

            if (_submittedToWriteEvents.Length > 0)
                PublishWriteEvents();
            else
                _awaitingWriteCompleted = false;
        }

        private bool DetectConcurrencyViolations(CheckpointTag expectedTag)
        {
            //NOTE: the comment below is not longer actual
            //      Keeping it for reference only
            //      We do back-read all the streams when loading state, so we know exactly which version to expect

            //TODO: if the following statement is about event order stream - let write null event into this stream
            //NOTE: the following condition is only meant to detect concurrency violations when
            // another instance of the projection (running in the another node etc) has been writing to 
            // the same stream.  However, the expected tag sometimes can be greater than last actually written tag
            // This happens when a projection is restarted from a checkpoint and the checkpoint has been made at 
            // position not updating the projection state 
            return expectedTag != _lastSubmittedOrCommittedMetadata;
        }

        private void PublishWriteEvents()
        {
            _writeDispatcher.Publish(
                new ClientMessage.WriteEvents(
                    Guid.NewGuid(), _writeDispatcher.Envelope, true, _streamId,
                    _lastKnownEventNumber, _submittedToWriteEvents), Handle);
        }

        private void EnsureCheckpointNotRequested()
        {
            if (_checkpointRequested)
                throw new InvalidOperationException("Checkpoint requested");
        }

        private void EnsureStreamStarted()
        {
            if (!_started)
                throw new InvalidOperationException("Not started");
        }

        private void OnWriteCompleted()
        {
            _awaitingWriteCompleted = false;
            NotifyWriteCompleted();
            ProcessWrites();
            ProcessRequestedCheckpoint();
        }

        private void NotifyWriteCompleted()
        {
            _readyHandler.Handle(new CoreProjectionProcessingMessage.EmittedStreamWriteCompleted(_streamId));
        }

        private void ProcessRequestedCheckpoint()
        {
            if (_checkpointRequested && !_awaitingWriteCompleted && _pendingWrites.Count == 0)
            {
                EnsureCheckpointsEnabled();
                _readyHandler.Handle(new CoreProjectionProcessingMessage.ReadyForCheckpoint(this));
            }
        }

        private void EnsureCheckpointsEnabled()
        {
            if (_noCheckpoints)
                throw new InvalidOperationException("Checkpoints disabled");
        }

        private void SubmitWriteEventsInRecovery()
        {
            bool anyFound = false;
            while (_pendingWrites.Count > 0)
            {
                var eventToWrite = _pendingWrites.Peek();
                if (eventToWrite.CausedByTag > _lastSubmittedOrCommittedMetadata || _alreadyCommittedEvents.Count == 0)
                    RecoveryCompleted();
                if (_recoveryCompleted)
                {
                    if (anyFound)
                        NotifyWriteCompleted(); // unlock pending write-resolves if any
                    SubmitWriteEvents();
                    return;
                }
                var topAlreadyCommitted = _alreadyCommittedEvents.Pop();
                ValidateEmittedEventInRecoveryMode(topAlreadyCommitted, eventToWrite);
                anyFound = true;
                NotifyEventCommitted(eventToWrite, topAlreadyCommitted.Item3); 
                _pendingWrites.Dequeue(); // drop already committed event
            }
            OnWriteCompleted();
        }

        private static void ValidateEmittedEventInRecoveryMode(Tuple<CheckpointTag, string, int> topAlreadyCommitted, EmittedEvent eventsToWrite)
        {
            if (topAlreadyCommitted.Item1 != eventsToWrite.CausedByTag || topAlreadyCommitted.Item2 != eventsToWrite.EventType)
                throw new InvalidOperationException(
                    string.Format(
                        "An event emitted in recovery differ from the originally emitted event.  Existing('{0}', '{1}'). New('{2}', '{3}')",
                        topAlreadyCommitted.Item2, topAlreadyCommitted.Item1, eventsToWrite.EventType, eventsToWrite.CausedByTag));
        }

        private void RecoveryCompleted()
        {
            _recoveryCompleted = true;
        }

        private static void NotifyEventsCommitted(EmittedEvent[] events, int firstEventNumber)
        {
            var sequenceNumber = firstEventNumber;
            foreach (var e in events)
                NotifyEventCommitted(e, sequenceNumber++);
        }

        private static void NotifyEventCommitted(EmittedEvent @event, int eventNumber)
        {
            if (@event.OnCommitted != null)
                @event.OnCommitted(eventNumber);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        public void Handle(CoreProjectionProcessingMessage.EmittedStreamWriteCompleted message)
        {
            if (!_awaitingReady)
                throw new InvalidOperationException("AwaitingReady state required");
            ProcessWrites();
        }
    }
}
