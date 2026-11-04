using System;
using System.Windows;

namespace QuickNote;

public partial class UpdateNetworkWindow : Window
{
    public UpdateNetworkWindow()
    {
        InitializeComponent();
        var proxy = UpdateCoordinator.ProxyAddress;
        if (proxy is not null) { CustomProxy.IsChecked = true; Address.Text = proxy; }
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CustomProxy.IsChecked == true && string.IsNullOrWhiteSpace(Address.Text))
            { Hint.Text = "请填写实际代理地址，或选择跟随系统代理。"; return; }
            UpdateCoordinator.SaveProxy(CustomProxy.IsChecked == true ? Address.Text : null);
            DialogResult = true;
        }
        catch (Exception ex) { Hint.Text = "设置未保存：" + ex.Message; }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
