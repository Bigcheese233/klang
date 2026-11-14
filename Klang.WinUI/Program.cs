using System;

namespace Klang_WinUI;

/// <summary>
/// 程序入口（替代 XAML 自动生成的 Main）：
/// - 带 <see cref="Services.AnalysisWorker.WorkerArg"/> 参数启动时，作为后台分析子进程运行，不加载界面；
/// - 否则照常启动 WinUI 应用。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Services.AnalysisWorker.WorkerArg)
            return Services.AnalysisWorker.RunWorker();

        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }
}
