@echo off
    path %~dp0..\..\v8\third_party\python_26\;C:\Windows\Microsoft.NET\Framework\v4.0.30319\;c:\Program Files (x86)\Git\bin;%PATH%; || goto :error
    if exist "C:\Program Files (x86)\Microsoft Visual Studio 11.0\VC\bin\vcvars32.bat" (
        set envconf="C:\Program Files (x86)\Microsoft Visual Studio 11.0\VC\bin\vcvars32.bat"
        set platformtoolset=v110
        set VisualStudioVersion=11.0
    ) else (
        if exist "C:\Program Files (x86)\Microsoft Visual Studio 10.0\VC\bin\vcvars32.bat" (
            set envconf="C:\Program Files (x86)\Microsoft Visual Studio 10.0\VC\bin\vcvars32.bat"
            set platformtoolset=v100
            set VisualStudioVersion=10.0
        ) else (
            echo "No visual Studio C++ build tools detected"
            goto :error
        )
    )
    echo Configuring C++ build with %envconf%
    call %envconf% || goto :error

    exit /b 0

:error
  echo Failed to configure C++ build environment
  exit /b 1