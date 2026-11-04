namespace ZhifuWater.Desktop.Ui;

/// <summary>
/// 页面可以在跳转时带上要聚焦的对象（工艺单元 / 设备 / 告警 / 工单），
/// 这样从驾驶舱点某台设备进去，看到的就是那台设备，而不是页面里上一次停留的选中项。
/// </summary>
public interface IFocusablePage
{
    void FocusItem(string key);
}
