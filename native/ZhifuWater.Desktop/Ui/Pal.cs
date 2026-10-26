using System;
using System.Windows;
using System.Windows.Media;

namespace ZhifuWater.Desktop.Ui;

/// <summary>与 Styles.xaml 对应的代码侧调色板 / 色调映射。</summary>
public static class Pal
{
    public static Brush FromHex(string hex, double opacity = 1)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public static readonly Brush Ink = FromHex("#111A1F");
    public static readonly Brush Ink2 = FromHex("#26343D");
    public static readonly Brush Muted = FromHex("#5C6B75");
    public static readonly Brush Muted2 = FromHex("#8A979F");
    public static readonly Brush Line = FromHex("#D3DBE0");
    public static readonly Brush LineSoft = FromHex("#E8EDF0");
    public static readonly Brush Bg = FromHex("#ECEFF2");
    public static readonly Brush Surface = FromHex("#FFFFFF");
    public static readonly Brush Surface2 = FromHex("#F6F8F9");
    public static readonly Brush Accent = FromHex("#0B6BB5");
    public static readonly Brush AccentStrong = FromHex("#085291");
    public static readonly Brush AccentSoft = FromHex("#E9F2FA");
    public static readonly Brush Good = FromHex("#1C6B45");
    public static readonly Brush GoodSoft = FromHex("#E9F2EC");
    public static readonly Brush Warn = FromHex("#8A5A12");
    public static readonly Brush WarnSoft = FromHex("#F7EFE2");
    public static readonly Brush Bad = FromHex("#A32C22");
    public static readonly Brush BadSoft = FromHex("#F7E9E7");
    public static readonly Brush Info = FromHex("#2A5F8A");
    public static readonly Brush InfoSoft = FromHex("#ECF2F7");
    public static readonly Brush Sidebar = FromHex("#101A20");
    public static readonly Brush SidebarText = FromHex("#A9BCC4");

    public static Brush ToneFg(string tone) => tone switch
    {
        "good" => Good,
        "warn" => Warn,
        "bad" => Bad,
        "info" => Info,
        "muted" => Muted,
        "accent" => AccentStrong,
        _ => Ink2,
    };

    public static Brush ToneBg(string tone) => tone switch
    {
        "good" => GoodSoft,
        "warn" => WarnSoft,
        "bad" => BadSoft,
        "info" => InfoSoft,
        "muted" => Surface2,
        "accent" => AccentSoft,
        _ => Surface2,
    };

    public static string AlarmLevelText(string level) => level switch
    {
        "critical" => "紧急",
        "major" => "重要",
        "minor" => "次要",
        "info" => "提示",
        _ => level,
    };

    public static string AlarmLevelTone(string level) => level switch
    {
        "critical" => "bad",
        "major" => "bad",
        "minor" => "warn",
        "info" => "muted",
        _ => "muted",
    };

    public static string AlarmStatusText(string status) => status switch
    {
        "active" => "未处理",
        "acknowledged" => "已确认",
        "resolved" => "已恢复",
        "closed" => "已关闭",
        _ => status,
    };

    public static string AlarmStatusTone(string status) => status switch
    {
        "active" => "bad",
        "acknowledged" => "warn",
        "resolved" => "good",
        "closed" => "muted",
        _ => "muted",
    };

    public static string WorkOrderStatusText(string status) => status switch
    {
        "pending" => "待接单",
        "accepted" => "已接单",
        "processing" => "处理中",
        "done" => "已完成",
        "verified" => "已验收",
        "closed" => "已关闭",
        _ => status,
    };

    public static string WorkOrderStatusTone(string status) => status switch
    {
        "pending" => "warn",
        "accepted" => "info",
        "processing" => "info",
        "done" => "good",
        "verified" => "good",
        "closed" => "muted",
        _ => "muted",
    };

    public static string WorkOrderTypeText(string type) => type switch
    {
        "repair" => "维修",
        "maintain" => "保养",
        "calibrate" => "校准",
        _ => type,
    };

    public static string WorkOrderPriorityText(string priority) => priority switch
    {
        "high" => "高",
        "normal" => "中",
        "low" => "低",
        _ => priority,
    };

    public static string WorkOrderSourceText(string source) => source switch
    {
        "alarm" => "告警升级",
        "inspection" => "巡检发现",
        "manual" => "人工创建",
        "system" => "系统生成",
        "" => "人工创建",
        _ => source,
    };

    public static string DeviceTypeText(string type) => type switch
    {
        "METER" => "仪表",
        "PUMP" => "泵",
        "BLOWER" => "风机",
        "VALVE" => "阀门",
        "DOSER" => "计量泵",
        "MIXER" => "搅拌机",
        "PLC" => "控制柜",
        "GATEWAY" => "网关",
        "" => "—",
        _ => type,
    };

    public static string CommandStatusText(string status) => status switch
    {
        "pending_confirm" => "待确认",
        "pending_dispatch" => "待下发",
        "sent" => "已下发",
        "executing" => "执行中",
        "success" => "成功",
        "failed" => "失败",
        "timeout" => "超时",
        "cancelled" => "已取消",
        _ => status,
    };

    public static string CommandStatusTone(string status) => status switch
    {
        "success" => "good",
        "failed" => "bad",
        "timeout" => "bad",
        "cancelled" => "muted",
        "executing" => "info",
        "sent" => "info",
        _ => "warn",
    };

    public static string UnitStateText(string state) => state switch
    {
        "normal" => "正常",
        "attention" => "关注",
        "alarm" => "告警",
        "offline" => "离线",
        "no-instrument" => "无仪表",
        _ => state,
    };

    public static string UnitStateTone(string state) => state switch
    {
        "normal" => "good",
        "attention" => "warn",
        "alarm" => "bad",
        "offline" => "muted",
        "no-instrument" => "muted",
        _ => "muted",
    };

    public static string ParamStatusText(string status) => status switch
    {
        "pending" => "待审批",
        "approved" => "已批准",
        "rejected" => "已驳回",
        _ => status,
    };

    public static string LabStatusText(string status) => status switch
    {
        "pending_review" => "待复核",
        "reviewed" => "已复核",
        "rejected" => "已退回",
        _ => status,
    };

    public static string LabStatusTone(string status) => status switch
    {
        "pending_review" => "warn",
        "reviewed" => "good",
        "rejected" => "bad",
        _ => "muted",
    };

    public static string ReportTypeText(string type) => type switch
    {
        "daily" => "日报",
        "weekly" => "周报",
        "monthly" => "月报",
        _ => "报表",
    };

    public static string ReportStatusText(string status) => status switch
    {
        "ready" => "已生成",
        "draft" => "草稿",
        "generating" => "生成中",
        _ => status,
    };

    public static string InspectionStatusText(string status) => status switch
    {
        "pending" => "待执行",
        "done" => "已提交",
        "overdue" => "逾期",
        _ => status,
    };

    public static string InspectionStatusTone(string status) => status switch
    {
        "pending" => "warn",
        "done" => "good",
        "overdue" => "bad",
        _ => "muted",
    };

    public static string InspectionResultText(string result) => result switch
    {
        "ok" => "正常",
        "abnormal" => "异常",
        _ => result,
    };

    public static string InspectionResultTone(string result) => result switch
    {
        "ok" => "good",
        "abnormal" => "bad",
        _ => "muted",
    };

    public static string SiteStatusText(string status) => status switch
    {
        "online" => "运行中",
        "offline" => "离线",
        "maintenance" => "维护中",
        _ => status,
    };

    public static string UserStatusText(string status) => status switch
    {
        "active" => "启用",
        "inactive" => "停用",
        _ => status,
    };
}
