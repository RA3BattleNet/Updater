namespace Ra3.BattleNet.Updater.Share.Log;

public static class Logger
{
    public static bool IsDebug { get; set; }

    public static void Info(string message)    => Serilog.Log.Information(message.TrimEnd());
    public static void Debug(string message)   => Serilog.Log.Debug(message.TrimEnd());
    public static void Warning(string message) => Serilog.Log.Warning(message.TrimEnd());
    public static void Success(string message) => Serilog.Log.Information(message.TrimEnd());
    public static void Fail(string message)    => Serilog.Log.Fatal(message.TrimEnd());
    public static void Note(string message)    => Serilog.Log.Information(message.TrimEnd());
    public static void Error(string message)   => Serilog.Log.Error(message.TrimEnd());
}
