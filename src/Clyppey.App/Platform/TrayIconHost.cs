using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Clyppey.App.Platform;

/// <summary>System tray presence: the app lives here while it buffers.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;

    public TrayIconHost(Action show, Action save, Action toggleRecord, Action toggleBuffer, Action openFolder, Action quit)
    {
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add(CreateItem("Clyppey を開く", show));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem("今すぐ保存", save));
        _menu.Items.Add(CreateItem("録画 開始 / 停止", toggleRecord));
        _menu.Items.Add(CreateItem("バッファ 開始 / 停止", toggleBuffer));
        _menu.Items.Add(CreateItem("保存先を開く", openFolder));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem("終了", quit));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Clyppey — インスタントリプレイ",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => show();
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Core.Log.Error($"トレイ操作でエラー: {text}", ex);
            }
        };
        return item;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/clyppey.ico"));
            if (resource is not null)
            {
                return new Icon(resource.Stream);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"トレイアイコンの読み込みに失敗: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception)
        {
            // Balloons are best effort.
        }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
        catch (Exception)
        {
            // Ignore.
        }
    }
}
