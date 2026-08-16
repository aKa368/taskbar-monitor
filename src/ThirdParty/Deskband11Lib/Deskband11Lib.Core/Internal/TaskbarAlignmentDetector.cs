using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Deskband11Lib.Core.Internal;

internal static class TaskbarAlignmentDetector
{
    private const string AdvancedRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskbarAlignmentValueName = "TaskbarAl";
    private const double LeftAlignmentThresholdRatio = 0.15;

    public static TaskbarAlignment Detect(HWND taskbarWindow, ButtonSpan startButtonSpan)
    {
        if (!taskbarWindow.IsNull && PInvoke.GetWindowRect(taskbarWindow, out var taskbarRectangle))
            return Detect(taskbarRectangle, startButtonSpan, ReadRegistryAlignment()).Alignment;
        return new AlignmentDecision(ReadRegistryAlignment(), AlignmentSource.RegistryFallback).Alignment;
    }

    internal static AlignmentDecision Detect(RECT taskbarRectangle, ButtonSpan startButtonSpan, TaskbarAlignment registryAlignment)
    {
        var inferred = InferAlignmentFromPosition(taskbarRectangle, startButtonSpan);
        return inferred != TaskbarAlignment.Unknown
            ? new AlignmentDecision(inferred, AlignmentSource.LiveGeometry)
            : new AlignmentDecision(registryAlignment, registryAlignment == TaskbarAlignment.Unknown ? AlignmentSource.Unavailable : AlignmentSource.RegistryFallback);
    }

    public static TaskbarAlignment ReadRegistryAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedRegistryKeyPath);
            if (key?.GetValue(TaskbarAlignmentValueName) is int alignmentValue) return alignmentValue switch
            {
                0 => TaskbarAlignment.Left,
                1 => TaskbarAlignment.Center,
                _ => TaskbarAlignment.Unknown
            };
        }
        catch { }

        return TaskbarAlignment.Unknown;
    }

    private static TaskbarAlignment InferAlignmentFromPosition(RECT taskbarRectangle, ButtonSpan startButtonSpan)
    {
        if (!startButtonSpan.IsValid) return TaskbarAlignment.Unknown;

        var taskbarWidth = taskbarRectangle.right - taskbarRectangle.left;
        if (taskbarWidth <= 0) return TaskbarAlignment.Unknown;

        return startButtonSpan.Left - taskbarRectangle.left < taskbarWidth * LeftAlignmentThresholdRatio ? TaskbarAlignment.Left : TaskbarAlignment.Center;
    }
}

internal enum AlignmentSource { Unavailable, LiveGeometry, NativeExplorerFallback, RegistryFallback }
internal readonly record struct AlignmentDecision(TaskbarAlignment Alignment, AlignmentSource Source);
