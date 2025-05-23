namespace Ra3.BattleNet.Updater.Share
{
    public static class Logger
    {
#if DEBUG
        public static bool IsDebug { get; set; } = true;
#else
        public static bool IsDebug = false; // 可通过参数修改
#endif
        public static string Path { get; set; } = string.Empty;
        private static bool WriteFileflag = true;
        private static void WriteToFile(string log)
        {
            if (!string.IsNullOrEmpty(Path))
            {
                try
                {
                    File.AppendAllText(Path, log);
                    if (WriteFileflag)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"[INFO] 日志路径：{Path}。");
                        Console.ResetColor();
                        WriteFileflag = false;
                    }
                }
                catch
                {
                    if (WriteFileflag)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[+] 日志文件写入失败，请检查路径是否正确或权限是否足够。");
                        Console.ResetColor();
                        WriteFileflag = false;
                    }
                }
            }
        }

        public static void Info(string msg)
        {
            string log = $"[INFO] {msg}";
            if (IsDebug)
            {
                var oldBg = Console.BackgroundColor;
                var oldFg = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.Write(log);
                Console.ResetColor();
            }
            WriteToFile(log + Environment.NewLine);
        }

        public static void Note(string msg)
        {
            string log = $"[NOTE] {msg}";
            if (IsDebug)
            {
                var oldBg = Console.BackgroundColor;
                var oldFg = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(log);
                Console.ResetColor();
            }
            WriteToFile(log + Environment.NewLine);
        }

        public static void Success(string msg)
        {
            string log = $"[+] {msg}";
            var oldBg = Console.BackgroundColor;
            var oldFg = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(log);
            Console.ResetColor();
            WriteToFile(log + Environment.NewLine);
        }

        public static void Fail(string msg)
        {
            string log = $"[-] {msg}";
            var oldBg = Console.BackgroundColor;
            var oldFg = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(log);
            Console.ResetColor();
            WriteToFile(log + Environment.NewLine);
        }

        public static void Debug(string msg)
        {
            string log = $"[DEBUG]: {msg}";
            if (IsDebug)
            {
                var oldBg = Console.BackgroundColor;
                var oldFg = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(log);
                Console.ResetColor();
            }
            WriteToFile(log + Environment.NewLine);
        }

        public static void Warning(string msg)
        {
            string log = $"[WARNING] {msg}";

            var oldBg = Console.BackgroundColor;
            var oldFg = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write(log);
            Console.ResetColor();

            WriteToFile(log + Environment.NewLine);
        }

        public static void Alert(string msg)
        {
            string log = $"[AlERT] {msg}";

            var oldBg = Console.BackgroundColor;
            var oldFg = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(log);
            Console.ResetColor();

            WriteToFile(log + Environment.NewLine);
        }

        public static void Ans(string msg)
        {
            string log = $"[ANSWER] {msg}";

            var oldBg = Console.BackgroundColor;
            var oldFg = Console.ForegroundColor;
            Console.BackgroundColor = ConsoleColor.White;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write(log);
            Console.ResetColor();

            WriteToFile(log + Environment.NewLine);
        }
    }
}
