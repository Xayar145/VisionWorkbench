using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.AI;

namespace VisionWorkbench.Views;

public class AiSettingsWindow : Window
{
    public AiSettings Settings { get; private set; }

    public AiSettingsWindow(AiSettings current)
    {
        Settings = current;
        Title = "AI 连接设置"; Width = 490; Height = 510; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White; FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "OpenAI 兼容接口", FontSize = 19, FontWeight = FontWeights.Bold, Margin = new(0, 0, 0, 17) });
        var endpoint = AddField(panel, "接口基础地址", current.Endpoint);
        var model = AddField(panel, "模型名称", current.Model);
        panel.Children.Add(new TextBlock { Text = "访问密钥", Margin = new(0, 0, 0, 5) });
        var key = new PasswordBox { Password = current.ApiKey, Padding = new(7), Margin = new(0, 0, 0, 12) }; panel.Children.Add(key);
        panel.Children.Add(new TextBlock { Text = "密钥仅保存在本机，使用 Windows 用户加密。", Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new(0, 0, 0, 18) });
        var save = new Button { Content = "保存连接", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true }; panel.Children.Add(save);
        save.Click += (_, _) => { Settings = new AiSettings { Endpoint = endpoint.Text.Trim(), Model = model.Text.Trim(), ApiKey = key.Password }; DialogResult = true; };
    }

    private static TextBox AddField(StackPanel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 5) });
        var box = new TextBox { Text = value, Margin = new(0, 0, 0, 12) }; panel.Children.Add(box); return box;
    }
}
