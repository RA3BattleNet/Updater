using System.Text.Json.Serialization;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 结果 JSON（`UpdateResult.ToJson()`）的**具名**形态。
///
/// 【必须】不能是匿名类型：`System.Text.Json` 在 Native AOT / 裁剪下只走 **source generation**，
/// 匿名类型无法在编译期注册（AOT 下反射被禁 → 运行时直接抛）；另外混淆会改掉匿名类型的构造参数名
/// （宿主那边混淆笔记里也有同一条）。字段名与顺序是对外契约：只增不改，新字段一律放末尾。
/// </summary>
internal sealed record UpdateResultJson(
    string Outcome, string Reason, string Detail, string HttpVersion,
    bool Applied, bool PendingRestart,
    int Total, int Skipped, int Moved, int Patched, int Full, int FailedCount,
    long BytesDownloaded, long PayloadBytes, long WireBytes, long WireSentBytes, long WireReceivedBytes,
    long Ms, int Deleted);

/// <summary>
/// `UpdaterStage/plan.json` 的序列化上下文（**格式与历史逐字一致**：缩进 + 枚举写字面量）。
/// 【必须】用 source generation 而不是反射：AOT/裁剪下反射式 JSON 会失效，而 `LoadPlan` 的
/// catch-all 会把那个异常吞成"没有计划" —— 落地于是**静默不生效**（实测过：AOT 产物对着一个
/// 合法 plan.json 报 `UpToDate` + "没有可用的待提交计划"）。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StagedPlan))]
internal partial class StagedPlanJsonContext : JsonSerializerContext
{
}

/// <summary>结果 JSON 的序列化上下文（紧凑输出，字段顺序 = 声明顺序）。</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UpdateResultJson))]
internal partial class UpdateResultJsonContext : JsonSerializerContext
{
}