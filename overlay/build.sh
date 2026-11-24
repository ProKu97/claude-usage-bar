#!/bin/sh
# 在本文件夹里运行：sh build.sh
F=/c/Windows/Microsoft.NET/Framework64/v4.0.30319
MSYS2_ARG_CONV_EXCL="*" $F/csc.exe -nologo -target:winexe -codepage:65001 -optimize+ -win32manifest:app.manifest -out:UsageBarOverlay.exe \
  -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll \
  -reference:C:/Windows/Microsoft.NET/Framework64/v4.0.30319/System.Web.Extensions.dll -reference:C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF/UIAutomationClient.dll \
  -reference:C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF/UIAutomationTypes.dll -reference:C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF/WindowsBase.dll UsageBarOverlay.cs
