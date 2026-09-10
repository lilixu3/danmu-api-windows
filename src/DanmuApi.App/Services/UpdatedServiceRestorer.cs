using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public static class UpdatedServiceRestorer
{
    public static async Task RestoreAsync(bool resumeService, IRuntimeController controller, string reportPath, IAppDiagnostics diagnostics, IUiDialogService dialogs)
    {
        if (!resumeService) return;
        string result;
        try
        {
            if (controller.Snapshot.State != DesktopRuntimeState.Running)
                await controller.StartAsync().ConfigureAwait(true);
            if (controller.Snapshot.State != DesktopRuntimeState.Running)
                throw new InvalidOperationException(controller.Snapshot.FailureReason ?? $"服务状态为 {controller.Snapshot.State}");
            result = "软件更新已完成，已恢复更新前运行的弹幕服务。";
            diagnostics.Record(result);
            File.WriteAllText(reportPath, result);
        }
        catch (Exception error)
        {
            result = "软件更新已完成，但服务恢复失败：" + error.Message;
            diagnostics.Record(result, error);
            try { File.WriteAllText(reportPath, result); }
            catch (Exception reportError) { diagnostics.Record("写入服务恢复报告失败", reportError); result += "；恢复报告写入失败：" + reportError.Message; }
            await dialogs.ShowMessageAsync("软件已更新，服务需要处理", result, isError: true).ConfigureAwait(true);
        }
    }
}
