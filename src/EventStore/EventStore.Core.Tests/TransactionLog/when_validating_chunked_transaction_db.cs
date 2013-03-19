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
using System.IO;
using EventStore.Core.Exceptions;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog
{
    [TestFixture]
    public class when_validating_chunked_transaction_db : SpecificationWithDirectory
    {
        [Test]
        public void with_file_of_wrong_size_database_corruption_is_detected()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(500),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            File.WriteAllText(GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), "this is just some test blahbydy blah");
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<BadChunkInDatabaseException>(ex.InnerException);
            db.Dispose();
        }

        [Test, Ignore("Not valid test now after disabling size validation on ongoing TFChunk ")]
        public void with_wrong_actual_chunk_size_in_chunk_footer()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             12000,
                                             0,
                                             new InMemoryCheckpoint(10000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), 10000, 12000);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<BadChunkInDatabaseException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void with_not_enough_files_to_reach_checksum_throws()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(15000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            var exc = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<ChunkNotFoundException>(exc.InnerException);
            db.Dispose();
        }

        [Test]
        public void allows_with_exactly_enough_file_to_reach_checksum()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(10000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            db.Dispose();
        }

        [Test]
        public void allows_next_new_chunk_when_checksum_is_exactly_in_between_two_chunks()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(10000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(1, 0)), config.ChunkSize, config.ChunkSize);
            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            db.Dispose();
        }

        [Test]
        public void allows_last_chunk_to_be_not_completed_when_checksum_is_exactly_in_between_two_chunks_and_no_next_chunk_exists()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(10000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateOngoingChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            db.Dispose();
        }

        [Test]
        public void does_not_allow_pre_last_chunk_to_be_not_completed_when_checksum_is_exactly_in_between_two_chunks_and_next_chunk_exists()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(10000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateOngoingChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            CreateOngoingChunk(1, 1, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(1, 0)), config.ChunkSize, config.ChunkSize);
            Assert.That(() => db.Open(verifyHash: false),
                        Throws.Exception.InstanceOf<CorruptDatabaseException>()
                        .With.InnerException.InstanceOf<BadChunkInDatabaseException>());
            db.Dispose();
        }

        [Test, Ignore("Not valid test now after disabling size validation on ongoing TFChunk ")]
        public void with_wrong_size_file_less_than_checksum_throws()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(15000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(1, 0)), config.ChunkSize-1000, config.ChunkSize);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<BadChunkInDatabaseException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void when_in_first_extraneous_files_throws_corrupt_database_exception()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(9000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(1, 0)), config.ChunkSize, config.ChunkSize);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<ExtraneousFileFoundException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void when_in_multiple_extraneous_files_throws_corrupt_database_exception()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(15000),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));

            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(1, 0)), config.ChunkSize, config.ChunkSize);
            CreateChunk(2, 2, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(2, 0)), config.ChunkSize, config.ChunkSize);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<ExtraneousFileFoundException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void when_in_brand_new_extraneous_files_throws_corrupt_database_exception()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(0),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(4, 4, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(4, 0)), config.ChunkSize, config.ChunkSize);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<ExtraneousFileFoundException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void when_a_chaser_checksum_is_ahead_of_writer_checksum_throws_corrupt_database_exception()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(0),
                                             new InMemoryCheckpoint(11),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<ReaderCheckpointHigherThanWriterException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void when_an_epoch_checksum_is_ahead_of_writer_checksum_throws_corrupt_database_exception()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(0),
                                             new InMemoryCheckpoint(0),
                                             new InMemoryCheckpoint(11),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<ReaderCheckpointHigherThanWriterException>(ex.InnerException);
            db.Dispose();
        }

        [Test]
        public void allows_no_files_when_checkpoint_is_zero()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            db.Dispose();
        }

        [Test]
        public void allows_first_correct_file_when_checkpoint_is_zero()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new PrefixFileNamingStrategy(PathName, "prefix.tf"),
                                             10000,
                                             0,
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);
            CreateChunk(0, 0, GetFilePathFor(config.FileNamingStrategy.GetFilenameFor(0, 0)), config.ChunkSize, config.ChunkSize);
            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            db.Dispose();
        }

        [Test]
        public void old_version_of_chunks_are_removed()
        {
            File.Create(GetFilePathFor("foo")).Close();
            File.Create(GetFilePathFor("bla")).Close();

            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(350),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000002"), config.ChunkSize, config.ChunkSize);
            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000005"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize, config.ChunkSize);
            CreateChunk(2, 2, GetFilePathFor("chunk-000002.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(3, 3, GetFilePathFor("chunk-000003.000007"), config.ChunkSize, config.ChunkSize);
            CreateChunk(3, 3, GetFilePathFor("chunk-000003.000008"), config.ChunkSize, config.ChunkSize);

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));

            Assert.IsTrue(File.Exists(GetFilePathFor("foo")));
            Assert.IsTrue(File.Exists(GetFilePathFor("bla")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000005")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000002.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000003.000008")));
            Assert.AreEqual(6, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        [Test]
        public void when_checkpoint_is_on_boundary_of_chunk_last_chunk_is_preserved()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(200),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize, config.ChunkSize);
            CreateChunk(2, 2, GetFilePathFor("chunk-000002.000005"), config.ChunkSize, config.ChunkSize);

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));

            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000002.000005")));
            Assert.AreEqual(3, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        [Test]
        public void when_checkpoint_is_on_boundary_of_new_chunk_last_chunk_is_preserved_and_excessive_versions_are_removed_if_present()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(200),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize, config.ChunkSize);
            CreateChunk(2, 2, GetFilePathFor("chunk-000002.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(2, 2, GetFilePathFor("chunk-000002.000001"), config.ChunkSize, config.ChunkSize);

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));

            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000002.000001")));
            Assert.AreEqual(3, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        [Test]
        public void when_checkpoint_is_exactly_on_the_boundary_of_chunk_the_last_chunk_could_be_not_present_but_should_be_created()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(200),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize, config.ChunkSize);

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            Assert.IsNotNull(db.Manager.GetChunk(2));

            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000002.000000")));
            Assert.AreEqual(3, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        [Test]
        public void when_checkpoint_is_exactly_on_the_boundary_of_chunk_the_last_chunk_could_be_present()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(200),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize, config.ChunkSize);
            CreateOngoingChunk(2, 2, GetFilePathFor("chunk-000002.000000"), config.ChunkSize, config.ChunkSize);

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            Assert.IsNotNull(db.Manager.GetChunk(2));

            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000002.000000")));
            Assert.AreEqual(3, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        [Test]
        public void when_checkpoint_is_on_boundary_of_new_chunk_and_last_chunk_is_truncated_no_exception_is_thrown()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(200),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize - 10, config.ChunkSize);

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));
            Assert.IsNotNull(db.Manager.GetChunk(2));

            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000002.000000")));
            Assert.AreEqual(3, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        [Test, Ignore("Not valid test now after disabling size validation on ongoing TFChunk ")]
        public void when_checkpoint_is_on_boundary_of_new_chunk_and_last_chunk_is_truncated_but_not_completed_exception_is_thrown()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(200),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateOngoingChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize - 10, config.ChunkSize);

            var ex = Assert.Throws<CorruptDatabaseException>(() => db.Open(verifyHash: false));
            Assert.IsInstanceOf<BadChunkInDatabaseException>(ex.InnerException);

            db.Dispose();
        }

        [Test]
        public void temporary_files_are_removed()
        {
            var config = new TFChunkDbConfig(PathName,
                                             new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                             100,
                                             0,
                                             new InMemoryCheckpoint(150),
                                             new InMemoryCheckpoint(),
                                             new InMemoryCheckpoint(-1),
                                             new InMemoryCheckpoint(-1));
            var db = new TFChunkDb(config);

            CreateChunk(0, 0, GetFilePathFor("chunk-000000.000000"), config.ChunkSize, config.ChunkSize);
            CreateChunk(1, 1, GetFilePathFor("chunk-000001.000001"), config.ChunkSize, config.ChunkSize);

            File.Create(GetFilePathFor("bla")).Close();
            File.Create(GetFilePathFor("bla.scavenge.tmp")).Close();
            File.Create(GetFilePathFor("bla.tmp")).Close();

            Assert.DoesNotThrow(() => db.Open(verifyHash: false));

            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000000.000000")));
            Assert.IsTrue(File.Exists(GetFilePathFor("chunk-000001.000001")));
            Assert.IsTrue(File.Exists(GetFilePathFor("bla")));
            Assert.AreEqual(3, Directory.GetFiles(PathName, "*").Length);

            db.Dispose();
        }

        private void CreateChunk(int chunkStartNum, int chunkEndNum, string filename, int actualSize, int chunkSize)
        {
            var chunkHeader = new ChunkHeader(TFChunk.CurrentChunkVersion, chunkSize, chunkStartNum, chunkEndNum, false, Guid.NewGuid());
            var chunkBytes = chunkHeader.AsByteArray();
            var buf = new byte[ChunkHeader.Size + actualSize + ChunkFooter.Size];
            Buffer.BlockCopy(chunkBytes, 0, buf, 0, chunkBytes.Length);
            var chunkFooter = new ChunkFooter(true, true, actualSize, actualSize, 0, new byte[ChunkFooter.ChecksumSize]);
            chunkBytes = chunkFooter.AsByteArray();
            Buffer.BlockCopy(chunkBytes, 0, buf, buf.Length - ChunkFooter.Size, chunkBytes.Length);
            File.WriteAllBytes(filename, buf);
        }

        private void CreateOngoingChunk(int chunkStartNum, int chunkEndNum, string filename, int actualSize, int chunkSize)
        {
            var chunkHeader = new ChunkHeader(TFChunk.CurrentChunkVersion, chunkSize, chunkStartNum, chunkEndNum, false, Guid.NewGuid());
            var chunkBytes = chunkHeader.AsByteArray();
            var buf = new byte[ChunkHeader.Size + actualSize + ChunkFooter.Size];
            Buffer.BlockCopy(chunkBytes, 0, buf, 0, chunkBytes.Length);
            File.WriteAllBytes(filename, buf);
        }
    }
}