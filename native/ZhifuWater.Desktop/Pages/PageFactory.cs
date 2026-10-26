using System;
using ZhifuWater.Desktop.Ui;
using ZhifuWater.Desktop.Windows;

namespace ZhifuWater.Desktop.Pages;

public static class PageFactory
{
    public static PageBase Create(string id, AppServices services, MainWindow host) => id switch
    {
        "dashboard" => new DashboardPage(services, host),
        "process" => new ProcessPage(services, host),
        "monitor" => new MonitorPage(services, host),
        "alarms" => new AlarmsPage(services, host),
        "control" => new ControlPage(services, host),
        "params" => new ParamsPage(services, host),
        "quality" => new QualityPage(services, host),
        "diagnosis" => new DiagnosisPage(services, host),
        "analysis" => new AnalysisPage(services, host),
        "reports" => new ReportsPage(services, host),
        "workorders" => new WorkOrdersPage(services, host),
        "inspections" => new InspectionsPage(services, host),
        "devices" => new DevicesPage(services, host),
        "admin" => new AdminPage(services, host),
        _ => throw new ArgumentException("未知页面：" + id, nameof(id)),
    };
}
