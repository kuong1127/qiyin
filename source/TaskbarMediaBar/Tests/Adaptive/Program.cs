using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using TaskbarMediaBar;
using TaskbarMediaBar.Interop;
using TaskbarMediaBar.Services;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        Directory.CreateDirectory(args[0]);
        Environment.SetEnvironmentVariable("TMB_DATA_DIR", args[0]);
        var results = new List<object>(); int failed = 0;
        void Check(string name, bool ok) { results.Add(new { name, passed = ok }); if (!ok) failed++; }
        NativeMethods.RECT Rect(int x,int y,int w,int h) => new(){Left=x,Top=y,Right=x+w,Bottom=y+h};
        foreach(double dpi in new[]{1.0,1.25,1.5,1.75,2,2.5,3})
        foreach(int height in new[]{24,32,40,48,64,80,96})
        foreach(int screen in new[]{640,1280,1920})
        foreach(string anchor in new[]{"left","center","beforeTray"})
        {
            int h=(int)Math.Round(height*dpi), w=(int)Math.Round(screen*dpi), x=-w;
            var bar=new TaskbarInfo{Rect=Rect(x,0,w,h),MonitorRect=Rect(x,0,w,1080),Visible=true,Scale=dpi};
            var tray=Rect(-120,0,120,h);
            var layout=AdaptiveTaskbarLayout.Compute(bar,new AppSettings{Anchor=anchor,Offset=2800},tray,Array.Empty<OccupantInfo>());
            Check($"bounds {dpi}/{height}/{screen}/{anchor}",layout.Fits && x+layout.X>=x && x+layout.X+layout.Width<=tray.Left
                && layout.Y>=0 && layout.Y+layout.Height<=h && layout.Width/layout.ContentScale/dpi>=219);
        }
        var baseBar=new TaskbarInfo{Rect=Rect(0,0,1000,48),Visible=true,Scale=1};
        var block=new OccupantInfo{Rect=Rect(280,0,500,48)};
        var fitted=AdaptiveTaskbarLayout.Compute(baseBar,new AppSettings(),Rect(900,0,100,48),new[]{block});
        Check("shrinks into free area without overlap",fitted.Fits && fitted.Width<360 && fitted.X+fitted.Width<280);
        var full=AdaptiveTaskbarLayout.Compute(baseBar,new AppSettings(),null,new[]{new OccupantInfo{Rect=Rect(0,0,1000,48)}});
        Check("no space hides instead of overlapping",!full.Fits);
        var hidden=new TaskbarInfo{Rect=Rect(0,1078,1920,48),MonitorRect=Rect(0,0,1920,1080),Visible=true};
        Check("auto-hidden strip does not show widget",!AdaptiveTaskbarLayout.Compute(hidden,new AppSettings(),null,Array.Empty<OccupantInfo>()).Fits);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=>
        {
            try
            {
                using var media=new MediaSessionService(Array.Empty<string>());
                var settings=new AppSettings{Glass=false,ShowLyrics=false,ShowLikeButton=false};
                var window=new MainWindow(settings,media){Left=-10000,Top=-10000}; window.Show();
                var snap=media.Current; snap.HasSession=true; snap.SourceAppId="UI_RENDER_ONLY";
                snap.Title="栖音 · 不同任务栏大小的自适应检查"; snap.Artist="歌词与操作按钮保留完整显示";
                snap.CanPlayPause=snap.CanPrev=snap.CanNext=snap.CanSeek=true; snap.DurationSeconds=200;snap.PositionSeconds=90;
                foreach(int h in new[]{24,28,32,40,54})
                foreach(int width in new[]{220,280,360,600})
                {
                    double factor=Math.Clamp(h/40.0,0.5,1.35);
                    window.Width=width*factor;window.Height=h;
                    window.ApplySnapshot(snap);
                    ((Button)window.FindName("LikeBtn")).Visibility=Visibility.Visible;
                    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                    bool contained=true;
                    foreach(var name in new[]{"PrevBtn","PlayBtn","NextBtn","LikeBtn"})
                    {
                        var b=(Button)window.FindName(name);
                        var bounds=b.TransformToAncestor(window).TransformBounds(new Rect(0,0,b.ActualWidth,b.ActualHeight));
                        contained &= bounds.Left>=-0.6 && bounds.Top>=-0.6 && bounds.Right<=window.ActualWidth+0.6 && bounds.Bottom<=window.ActualHeight+0.6;
                    }
                    Check($"buttons stay complete {width}x{h}",contained);
                    Check($"cover responsive {width}x{h}",((Border)window.FindName("Cover")).Visibility==(width<280?Visibility.Collapsed:Visibility.Visible));
                    var track=(Grid)window.FindName("ProgressTrack");
                    var pt=track.TranslatePoint(new Point(track.ActualWidth/2,3),window);
                    var hit=window.InputHitTest(pt) as DependencyObject;
                    while(hit is not null && !ReferenceEquals(hit,track))hit=VisualTreeHelper.GetParent(hit);
                    Check($"seek hit survives scaling {width}x{h}",ReferenceEquals(hit,track));
                    var encoder=new PngBitmapEncoder();
                    var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*1.5),(int)Math.Ceiling(window.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
                    image.Render(window);encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file=File.Create(Path.Combine(args[0],$"adaptive-{width}x{h}.png"));encoder.Save(file);
                }
                var hwnd=new WindowInteropHelper(window).Handle;
                double nativeDpi=NativeMethods.GetDpiForWindow(hwnd)/96.0;
                settings.AvoidOthers=false;
                foreach(int height in new[]{24,32,48,80,32,48})
                {
                    var nativeBar=new TaskbarInfo{Rect=Rect(-8000,-8000,(int)(1280*nativeDpi),(int)(height*nativeDpi)),Visible=true,Scale=nativeDpi};
                    TaskbarHost.PlaceOverlay(window,nativeBar,settings);
                    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    NativeMethods.GetWindowRect(hwnd,out var actual);
                    var expected=AdaptiveTaskbarLayout.Compute(nativeBar,settings,null,Array.Empty<OccupantInfo>());
                    Check($"native window follows resize {height}",actual.Width==expected.Width && actual.Height==expected.Height
                        && actual.Top>=nativeBar.Rect.Top && actual.Bottom<=nativeBar.Rect.Bottom);
                    var play=(Button)window.FindName("PlayBtn");
                    var bounds=play.TransformToAncestor(window).TransformBounds(new Rect(0,0,play.ActualWidth,play.ActualHeight));
                    Check($"native resized button contained {height}",bounds.Left>=0 && bounds.Top>=0
                        && bounds.Right<=window.ActualWidth+0.6 && bounds.Bottom<=window.ActualHeight+0.6);
                }
                TaskbarHost.PlaceOverlay(window,new TaskbarInfo{Visible=false},settings);
                Check("native auto-hide hides widget",!NativeMethods.IsWindowVisible(hwnd));
                TaskbarHost.PlaceOverlay(window,new TaskbarInfo{Rect=Rect(-8000,-8000,1920,72),Visible=true,Scale=1.5},settings);
                Check("native restore shows widget",NativeMethods.IsWindowVisible(hwnd));
                window.Close();
            }
            catch(Exception ex){failed++;results.Add(new{error=ex.ToString()});}
            finally {File.WriteAllText(Path.Combine(args[0],"checks.json"),JsonSerializer.Serialize(new{passed=results.Count-failed,total=results.Count,results},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        };
        app.Run();Console.WriteLine($"Adaptive checks: {results.Count-failed}/{results.Count}");return failed==0?0:1;
    }
}
