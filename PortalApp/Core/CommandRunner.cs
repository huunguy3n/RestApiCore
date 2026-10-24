using System.Text;
using PortalApp.Ui;

namespace PortalApp.Core
{
    public sealed record RunResult(int ExitCode, bool Completed);

    /// <summary>
    /// Chạy một lệnh Cli trong tiến trình, chuyển Console.Out/Error vào log. Chỉ cho một lệnh chạy tại một thời điểm
    /// (Console là tài nguyên toàn cục). Giá trị mật khẩu bị che nếu lỡ xuất hiện trong output.
    /// </summary>
    public sealed class CommandRunner
    {
        private bool busy;

        public bool IsBusy => busy;
        public event EventHandler? BusyChanged;

        public async Task<RunResult> RunAsync(CommandDef command, string[] args, string? password, ILogSink log)
        {
            if (busy) { return new RunResult(-1, false); }
            SetBusy(true);

            TextWriter oldOut = Console.Out, oldErr = Console.Error;
            var stdout = new LogWriter(log, LogLevel.Info, password);
            var stderr = new LogWriter(log, LogLevel.Error, password);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                if (password != null) { Environment.SetEnvironmentVariable(CommandLine.PasswordEnv, password); }
                int code = await Task.Run(() => command.Run(args));
                return new RunResult(code, true);
            }
            catch (Exception ex)
            {
                // Chỉ ghi Message; không ghi stack trace hay dữ liệu đầu vào
                log.Write(LogLevel.Error, Mask(ex.Message, password));
                return new RunResult(-1, false);
            }
            finally
            {
                Environment.SetEnvironmentVariable(CommandLine.PasswordEnv, null);
                stdout.FlushPending();
                stderr.FlushPending();
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string Mask(string text, string? secret) =>
            string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "••••••");

        private sealed class LogWriter(ILogSink sink, LogLevel level, string? secret) : TextWriter
        {
            private readonly StringBuilder line = new();
            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                if (value == '\n') { FlushPending(); }
                else if (value != '\r') { line.Append(value); }
            }

            public void FlushPending()
            {
                if (line.Length == 0) { return; }
                sink.Write(level, Mask(line.ToString(), secret));
                line.Clear();
            }
        }
    }
}
