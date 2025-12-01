#!/usr/bin/env dotnet-script
#r "nuget: FFmpeg.AutoGen, 7.0.0"

using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

Console.WriteLine("=== FFmpeg Loading Test ===");
Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine();

string baseDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
string ffmpegPath = Path.Combine(baseDirectory, "..", "..", "..", "HlaeObsTools", "bin", "Debug", "net8.0", "FFmpeg", "win-x64");
ffmpegPath = Path.GetFullPath(ffmpegPath);

Console.WriteLine($"FFmpeg path: {ffmpegPath}");
Console.WriteLine($"Directory exists: {Directory.Exists(ffmpegPath)}");
Console.WriteLine();

if (Directory.Exists(ffmpegPath))
{
    Console.WriteLine("DLL files found:");
    foreach (var file in Directory.GetFiles(ffmpegPath, "*.dll"))
    {
        Console.WriteLine($"  - {Path.GetFileName(file)} ({new FileInfo(file).Length:N0} bytes)");
    }
    Console.WriteLine();
}

Console.WriteLine("Setting FFmpeg root path...");
ffmpeg.RootPath = ffmpegPath;

Console.WriteLine("Attempting to call av_version_info()...");
try
{
    string version = ffmpeg.av_version_info();
    Console.WriteLine($"SUCCESS! FFmpeg version: {version}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED! Error: {ex.GetType().Name}");
    Console.WriteLine($"Message: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Full exception:");
    Console.WriteLine(ex.ToString());
}
