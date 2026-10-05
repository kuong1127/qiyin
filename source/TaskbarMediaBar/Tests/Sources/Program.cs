using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using TaskbarMediaBar.Services;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

internal static class Program
{
 [STAThread] private static int Main(string[] args)
 {
  var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
  if (args.Length == 2 && args[0] == "--provider")
  {
   using var player = new MediaPlayer { IsMuted = true, IsLoopingEnabled = true };
   string providerLog=Path.Combine(Path.GetDirectoryName(args[1])!,"provider.txt");
   player.MediaFailed += (_,e)=>File.AppendAllText(providerLog,"failed: "+e.ErrorMessage+"\n");
   player.PlaybackSession.PlaybackStateChanged += (s,_)=>File.AppendAllText(providerLog,"state: "+s.PlaybackState+"\n");
   player.Source = MediaSource.CreateFromUri(new Uri(args[1]));
   player.Play();
   var controls = player.SystemMediaTransportControls;
   controls.IsEnabled = true; controls.IsPlayEnabled = true; controls.IsPauseEnabled = true;
   controls.DisplayUpdater.Type = MediaPlaybackType.Music;
   controls.DisplayUpdater.MusicProperties.Title = "Qiyin isolated media";
   controls.DisplayUpdater.Update();
   return app.Run();
  }
  if(args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
  string output=args[0]; Directory.CreateDirectory(output);
  Environment.SetEnvironmentVariable("TMB_DATA_DIR", output);
  var results = new Dictionary<string,bool>();
  void Check(string name,bool passed) { results[name]=passed; if(!passed) throw new Exception(name); }
  app.Startup += async (_,_) =>
  {
   Process? provider=null;
   using var media=new MediaSessionService(new[]{"QQMusic","cloudmusic"});
   try
   {
    await media.InitAsync(); await media.RefreshAsync();
    string? qq=media.Sources.FirstOrDefault(s=>s.Label.StartsWith("QQ"))?.Key;
    string? browser=media.Sources.FirstOrDefault(s=>s.Label is "Edge" or "Chrome" or "Firefox")?.Key;
    var before=media.Sources.Select(s=>s.Key).ToHashSet();
    string wav=Path.Combine(output,"silent.wav");
    using(var writer=new BinaryWriter(File.Create(wav))) {
     const int length=44100*2*90;
     writer.Write("RIFF"u8);writer.Write(36+length);writer.Write("WAVEfmt "u8);writer.Write(16);
     writer.Write((short)1);writer.Write((short)1);writer.Write(44100);writer.Write(88200);writer.Write((short)2);writer.Write((short)16);
     writer.Write("data"u8);writer.Write(length);writer.Write(new byte[length]);
    }
    var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden};
    info.ArgumentList.Add("--provider");info.ArgumentList.Add(wav);provider=Process.Start(info)!;
    async Task Poll(Func<bool> condition) {for(int i=0;i<40;i++){await media.RefreshAsync();if(condition())return;await Task.Delay(100);}}
    await Poll(()=>media.Sources.Any(s=>!before.Contains(s.Key)));
    var added=media.Sources.FirstOrDefault(s=>!before.Contains(s.Key));
    Check("real system session registered",added is not null);
    string key=added!.Key;
    await Poll(()=>media.Current.SessionKey==key && media.Current.IsPlaying);
    File.WriteAllText(Path.Combine(output,"selection.json"),JsonSerializer.Serialize(new{media.Sources,media.Current.SessionKey,media.Current.SourceAppId,media.Current.IsPlaying,provider.HasExited}));
    Check("new playback automatically selected",media.Current.SessionKey==key && media.Current.IsPlaying);
    var stampField=typeof(MediaSessionService).GetField("_propertiesReadAt",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
    long stamp=(long)stampField.GetValue(media)!;
    await media.RefreshAsync();
    Check("unchanged metadata reused on polling",stamp==(long)stampField.GetValue(media)!);
    typeof(MediaSessionService).GetMethod("OnMediaPropertiesChanged",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(media,new object?[]{null,null});
    await Poll(()=>(long)stampField.GetValue(media)! != stamp);
    Check("metadata notification invalidates cache immediately",stamp!=(long)stampField.GetValue(media)!);
    for(int i=0;i<5;i++)await media.RefreshAsync();
    Check("session identity stable across enumeration",media.Current.SessionKey==key && media.Sources.Count(s=>s.Key==key)==1);
    Check("pause command accepted",await media.TogglePlayPauseAsync());
    await Poll(()=>!media.Current.IsPlaying);
    Check("pause keeps source with QQ in background",media.Current.SessionKey==key && !media.Current.IsPlaying);
    await Task.Delay(1100);await media.RefreshAsync();
    Check("paused source survives later polling",media.Current.SessionKey==key && !media.Current.IsPlaying);
    Check("resume command accepted",await media.TogglePlayPauseAsync());
    await Poll(()=>media.Current.IsPlaying);
    Check("resume controls original source",media.Current.SessionKey==key && media.Current.IsPlaying);
    if(qq is not null){media.SelectSource(qq);await Poll(()=>media.Current.SessionKey==qq);Check("manual QQ selection locks",media.Current.SessionKey==qq && media.SourceLocked);}
    if(browser is not null){media.SelectSource(browser);await Poll(()=>media.Current.SessionKey==browser);Check("manual browser selection locks",media.Current.SessionKey==browser && media.SourceLocked);}
    media.SelectSource(key);await Poll(()=>media.Current.SessionKey==key);
    Check("manual return to media source locks",media.SourceLocked && media.Current.SessionKey==key);
    media.SelectSource(null);await media.RefreshAsync();
    Check("automatic mode unlocks without surprise switch",!media.SourceLocked && media.Current.SessionKey==key);
    media.SelectSource(key);await media.RefreshAsync();
    provider.Kill();provider.WaitForExit();
    await Poll(()=>media.Current.SessionKey!=key);
    Check("closed selected source releases lock",!media.SourceLocked && media.Current.SessionKey!=key);
   }
   catch(Exception ex){results["completed without error"]=false;File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
   finally{
    if(provider is {HasExited:false})provider.Kill();
    File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    app.Shutdown(results.Values.All(v=>v)?0:1);
   }
  };
  return app.Run();
 }
}
