using ImmichTray.Settings;
using ImmichTray.UI;
using ImmichTray.Updates;

namespace ImmichTray.Tests;

public class SettingsFormTests
{
    private sealed class Host : ISettingsHost
    {
        public AppSettings CurrentSettings { get; } = new() { ServerUrl = "http://immich.local:2283" };
        public Updater Updater { get; } = new();
        public Task ApplySettingsAsync(AppSettings updated) => Task.CompletedTask;
        public Task UpdateNowAsync(IWin32Window? owner) => Task.CompletedTask;
    }

    /// <summary>Builds the window and draws every tab. Images go to %TEMP%\immichtray-ui for a visual check.</summary>
    [Fact]
    public void Settings_window_builds_and_renders_every_tab()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var outDir = Path.Combine(Path.GetTempPath(), "immichtray-ui");
                Directory.CreateDirectory(outDir);
                var host = new Host();
                using var form = new SettingsForm(host, host.CurrentSettings);
                form.Show();
                for (var i = 0; i < form.Tabs.TabCount; i++)
                {
                    form.Tabs.SelectedIndex = i;
                    Application.DoEvents();
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(Path.Combine(outDir, $"tab{i}-{form.Tabs.TabPages[i].Text}.png"));
                }
                form.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
    }
}
