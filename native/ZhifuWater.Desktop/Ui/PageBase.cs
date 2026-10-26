using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Windows;

namespace ZhifuWater.Desktop.Ui;

/// <summary>页面基类：统一脚注（数据来源 + 更新时间）、构建/刷新生命周期与表格工具。</summary>
public abstract class PageBase : UserControl
{
    protected readonly AppServices Svc;
    protected readonly MainWindow Host;
    private bool _built;
    private TextBlock _footnote = null!;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Group { get; }
    protected virtual string SourceLabel => "CoreServices 业务层";

    protected PageBase(AppServices services, MainWindow host)
    {
        Svc = services;
        Host = host;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Background = Pal.Bg;
    }

    public void RefreshPage()
    {
        if (!_built)
        {
            _built = true;
            Build();
            var dock = new DockPanel { LastChildFill = true };
            _footnote = new TextBlock { FontSize = 10.5, Foreground = Pal.Muted2, TextTrimming = TextTrimming.CharacterEllipsis };
            dock.Children.Add(new Border { Background = Pal.Bg, Child = _content });
            Content = dock;
        }
        RefreshData();
        UpdateFootnote();
    }

    private UIElement? _content;

    protected abstract void Build();
    protected abstract void RefreshData();

    /// <summary>页面内容装配入口：子类 Build 末尾调用。</summary>
    protected void Install(UIElement content)
    {
        _content = content;
    }

    protected void UpdateFootnote()
    {
        if (_footnote == null) return;
        _footnote.Text = $"数据来源：{Svc.BackendName} · {SourceLabel} · 站点：{Svc.Data.Site.Name}（{Svc.Data.Site.Id}） · " +
                         $"最后更新：{Svc.Data.LastUpdated:yyyy-MM-dd HH:mm:ss} · 每 5 秒自动刷新";
    }

    protected void Toast(string message, bool ok = true) => Host.Toast(message, ok);

    // ------------------------------------------------------------------
    // 表格工具
    // ------------------------------------------------------------------
    protected static DataGrid Table(string[] headers, double[] widths, Action<UiRow>? onSelect = null)
        => Kit.Table(headers, Array.Empty<UiRow>(), widths, onSelect);

    protected static void Bind(DataGrid grid, IEnumerable<UiRow> rows)
    {
        grid.ItemsSource = rows.ToList();
    }

    protected static string Num(double? v, int digits = 2, string suffix = "")
        => v.HasValue ? v.Value.ToString("F" + digits) + suffix : "待接入";

    protected static string ValueOf(Reading? reading, int digits = 2)
    {
        if (reading == null) return "待接入";
        if (!reading.Value.HasValue) return reading.Text ?? "待接入";
        return reading.Value.Value.ToString("F" + Math.Max(reading.Point.Digits, digits)) + (string.IsNullOrEmpty(reading.Point.Unit) ? "" : " " + reading.Point.Unit);
    }

    protected static Border Section(string title, UIElement body, UIElement? right = null, string? subtitle = null)
        => Kit.Card(title, body, right, subtitle);
}
