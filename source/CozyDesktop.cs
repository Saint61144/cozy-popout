using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Wire {
    internal const int Limit = 65536;
    internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Limit, RecursionLimit = 12 };
    internal static readonly object JsonLock = new object();
    internal static string Encode(object value) { lock(JsonLock) return Json.Serialize(value); }
    internal static Dictionary<string,object> Decode(string value) {
        if(value == null || Encoding.UTF8.GetByteCount(value)>Limit) throw new InvalidDataException("Message too large");
        lock(JsonLock) return Json.Deserialize<Dictionary<string,object>>(value);
    }
    internal static bool Exact(Stream stream, byte[] data, int count) {
        int offset=0;
        while(offset<count) { int got=stream.Read(data,offset,count-offset); if(got==0) { if(offset==0) return false; throw new EndOfStreamException(); } offset+=got; }
        return true;
    }
    internal static string Read(Stream stream) {
        byte[] prefix=new byte[4]; if(!Exact(stream,prefix,4)) return null;
        int count=BitConverter.ToInt32(prefix,0);
        if(count<2 || count>Limit) throw new InvalidDataException("Message size rejected");
        byte[] bytes=new byte[count]; if(!Exact(stream,bytes,count)) throw new EndOfStreamException();
        string text=new UTF8Encoding(false,true).GetString(bytes); Decode(text); return text;
    }
    internal static void Write(Stream stream,string text) {
        byte[] bytes=Encoding.UTF8.GetBytes(text);
        if(bytes.Length<2 || bytes.Length>Limit) throw new InvalidDataException("Message too large");
        byte[] prefix=BitConverter.GetBytes(bytes.Length); stream.Write(prefix,0,4); stream.Write(bytes,0,bytes.Length); stream.Flush();
    }
    internal static string Text(Dictionary<string,object> data,string name,string fallback) {
        object value; return data!=null && data.TryGetValue(name,out value) && value is string ? (string)value : fallback;
    }
    internal static bool Bool(Dictionary<string,object> data,string name) {
        object value; return data!=null && data.TryGetValue(name,out value) && value is bool && (bool)value;
    }
    internal static double Number(Dictionary<string,object> data,string name,double fallback) {
        object value; if(data==null || !data.TryGetValue(name,out value) || value==null || value is bool || value is string) return fallback;
        try { double number=Convert.ToDouble(value); return double.IsNaN(number)||double.IsInfinity(number)?fallback:number; } catch { return fallback; }
    }
    internal static bool Command(Dictionary<string,object> data) {
        string action=Text(data,"action",""); string key=Text(data,"key","");
        if(Text(data,"type","")!="command" || key.Length==0 || key.Length>200) return false;
        double value=Number(data,"value",double.NaN);
        switch(action) {
            case "play": case "mute": case "next": case "previous": return true;
            case "volume": return value>=0 && value<=1;
            case "seek": return value>=0 && value<=1e10;
            case "skip": return value==10 || value==-10;
            case "speed": return Array.IndexOf(new double[]{.25,.5,.75,1,1.25,1.5,1.75,2},value)>=0;
            default: return false;
        }
    }
}

internal static class Program {
    internal const string Host="com.cozy_popout.companion";
    internal static string Home { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    internal static string PipeName { get {
        string folder=Home.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        using(var hash=SHA256.Create()) {
            string suffix=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(folder)),0,8).Replace("-","");
            return "CozyDesktop_"+WindowsIdentity.GetCurrent().User.Value.Replace('-','_')+"_"+suffix;
        }
    } }
    internal static string Origin { get { return "chrome-extension://"+BuildIdentity.ExtensionId+"/"; } }
    internal static string RegistryPath(string browser) {
        string vendor=browser=="Chrome"?"Google\\Chrome":browser=="Edge"?"Microsoft\\Edge":"BraveSoftware\\Brave-Browser";
        return "Software\\"+vendor+"\\NativeMessagingHosts\\"+Host;
    }
    private static IEnumerable<string> RegistryPaths(string browser) {
        yield return RegistryPath(browser);
        // Brave can also look up per-user hosts in the Chrome location.
        if(browser=="Brave") yield return RegistryPath("Chrome");
    }
    internal static void OpenConnectionPage(string browser) {
        string executable=browser=="Brave"?"brave.exe":browser=="Edge"?"msedge.exe":"chrome.exe";
        string relative=browser=="Brave"?@"BraveSoftware\Brave-Browser\Application":
            browser=="Edge"?@"Microsoft\Edge\Application":@"Google\Chrome\Application";
        foreach(var folder in new[]{Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.LocalApplicationData}) {
            string candidate=Path.Combine(Environment.GetFolderPath(folder),relative,executable);
            if(File.Exists(candidate)){executable=candidate;break;}
        }
        Process.Start(new ProcessStartInfo(executable,Origin+"connect.html"){UseShellExecute=true});
    }
    [STAThread] private static int Main(string[] args) {
        if(Array.IndexOf(args,"--self-test")>=0) return SelfTest();
        foreach(string arg in args) if(arg.StartsWith("chrome-extension://",StringComparison.Ordinal)) {
            if(arg!=Origin) return 3;
            return NativeBridge();
        }
        bool created;
        using(Mutex mutex=new Mutex(true,"Local\\"+PipeName,out created)) {
            if(!created) { MessageBox.Show("Cozy Desktop is already running. Look for the small Reiner icon.","Cozy Desktop"); return 0; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ReinerForm());
            mutex.ReleaseMutex();
        }
        return 0;
    }
    internal static int NativeBridge() {
        try {
            using(NamedPipeClientStream pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)) {
                try { pipe.Connect(700); }
                catch(TimeoutException) {
                    Process.Start(new ProcessStartInfo(Path.Combine(Home,"CozyDesktop.exe"),"--desktop") {UseShellExecute=false,CreateNoWindow=true});
                    pipe.Connect(8000);
                }
                Stream input=Console.OpenStandardInput(), output=Console.OpenStandardOutput();
                Task outgoing=Task.Run(()=>{
                    try { string text; while((text=Wire.Read(pipe))!=null) {
                        var message=Wire.Decode(text);
                        if(Wire.Text(message,"type","")=="refresh" || Wire.Text(message,"type","")=="ready" || Wire.Command(message)) Wire.Write(output,text);
                    } } catch { }
                });
                Task incoming=Task.Run(()=>{
                    try { string text; while((text=Wire.Read(input))!=null) {
                        var message=Wire.Decode(text); string type=Wire.Text(message,"type","");
                        if(type=="state" || type=="error") Wire.Write(pipe,text);
                    } } catch { }
                });
                // Either browser EOF or closing the desktop app ends this host process.
                Task.WaitAny(incoming,outgoing);
            }
            return 0;
        } catch(Exception error) {
            try {byte[] detail=Encoding.UTF8.GetBytes("Cozy local connection failed: "+error.GetType().Name+"\n");Console.OpenStandardError().Write(detail,0,detail.Length);} catch { }
            return 2;
        }
    }
    internal static void Register(string browser) {
        string manifest=Path.Combine(Home,"Cozy.NativeHost.json");
        string json=Wire.Encode(new {name=Host,description="Cozy Desktop local playback connection",path=Path.Combine(Home,"CozyDesktop.exe"),type="stdio",allowed_origins=new[]{Origin}});
        File.WriteAllText(manifest,json,new UTF8Encoding(false));
        foreach(string path in RegistryPaths(browser)) {
            using(RegistryKey key=Registry.CurrentUser.CreateSubKey(path)) key.SetValue("",manifest,RegistryValueKind.String);
        }
    }
    internal static void Unregister(string browser) {
        foreach(string path in RegistryPaths(browser)) {
            using(RegistryKey key=Registry.CurrentUser.OpenSubKey(path)) {
                if(key==null || !String.Equals(Convert.ToString(key.GetValue("")),Path.Combine(Home,"Cozy.NativeHost.json"),StringComparison.OrdinalIgnoreCase)) continue;
            }
            Registry.CurrentUser.DeleteSubKey(path,false);
        }
    }
    internal static int SelfTest() {
        var passed=new List<string>();
        try {
            using(var memory=new MemoryStream()) { Wire.Write(memory,"{\"type\":\"state\",\"available\":false}"); memory.Position=0; if(!Wire.Read(memory).Contains("state")) throw new Exception("Framing"); }
            passed.Add("PASS: native/pipe UTF-8 length framing roundtrip");
            using(var memory=new MemoryStream(BitConverter.GetBytes(Wire.Limit+1))) { try { Wire.Read(memory); throw new Exception("Size not rejected"); } catch(InvalidDataException){} }
            passed.Add("PASS: oversized frames rejected");
            var command=new Dictionary<string,object>{{"type","command"},{"action","volume"},{"key","test"},{"value",.25}};
            if(!Wire.Command(command)) throw new Exception("Valid volume"); command["value"]=2; if(Wire.Command(command)) throw new Exception("Invalid volume");
            command["action"]="exec"; if(Wire.Command(command)) throw new Exception("Unknown action");
            passed.Add("PASS: bounded volume and action allowlist");
            if(Origin.Length!=52 || BuildIdentity.ExtensionId.Length!=32) throw new Exception("Origin");
            passed.Add("PASS: exact extension origin configured");
            Application.EnableVisualStyles();
            using(var icon=new ReinerForm(false)) {
                if(!icon.TopMost || icon.ShowInTaskbar || icon.FormBorderStyle!=FormBorderStyle.None) throw new Exception("Overlay style");
                using(var panel=new ControlsForm(icon)) { if(!panel.TopMost || panel.Width!=246) throw new Exception("Panel style"); }
            }
            passed.Add("PASS: native overlay and compact panel construct without registration");
            using(Stream source=Assembly.GetExecutingAssembly().GetManifestResourceStream("Reiner.png"))
            using(var image=new Bitmap(source))
            using(var bitmap=AlphaWindow.Render(image)) {
                using(var window=new ReinerForm(false))AlphaWindow.Apply(window,image);
                bool partial=false,visible=false;
                for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++){
                    Color pixel=bitmap.GetPixel(x,y);
                    if(pixel.A>0)visible=true;if(pixel.A>0 && pixel.A<255)partial=true;
                    if(pixel.A>0 && pixel.R==255 && pixel.G==0 && pixel.B==255)throw new Exception("Magenta fringe");
                }
                if(bitmap.GetPixel(0,0).A!=0 || !partial || !visible)throw new Exception("Alpha rendering");
            }
            using(var setup=new SetupForm()) {int buttons=0;foreach(Control control in setup.Controls)if(control is Button){buttons++;if(control.Text!="Connect")throw new Exception("Setup button");}if(buttons!=1)throw new Exception("Single Connect");}
            passed.Add("PASS: transparent icon render without color-key matte and single Connect setup");
            var networkSid=new SecurityIdentifier(WellKnownSidType.NetworkSid,null);
            var pipeRules=LocalConnection.CreatePipeSecurity().GetAccessRules(true,false,typeof(SecurityIdentifier));
            bool deniesNetwork=false;
            foreach(PipeAccessRule rule in pipeRules) {
                if(rule.IdentityReference.Equals(networkSid) && rule.AccessControlType==AccessControlType.Deny &&
                   (rule.PipeAccessRights & PipeAccessRights.ReadWrite)==PipeAccessRights.ReadWrite) deniesNetwork=true;
            }
            if(!deniesNetwork)throw new Exception("Pipe must reject network logons");
            passed.Add("PASS: Windows named pipe access rules deny network logons");
            using(var connected=new ManualResetEvent(false))
            using(var received=new ManualResetEvent(false))
            using(var local=new LocalConnection(Program.PipeName+"_test_"+Guid.NewGuid().ToString("N"))) {
                local.Connected+=value=>{if(value)connected.Set();};
                local.Received+=value=>{if(Wire.Text(value,"key","")=="test")received.Set();};
                local.Start();
                using(var client=new NamedPipeClientStream(".",local.Name,PipeDirection.InOut,PipeOptions.Asynchronous)) {
                    client.Connect(3000);if(!connected.WaitOne(3000))throw new Exception("Pipe connection");
                    Wire.Write(client,"{\"type\":\"state\",\"key\":\"test\"}");if(!received.WaitOne(3000))throw new Exception("Pipe receive");
                    if(!local.Send(new {type="ready"}) || Wire.Text(Wire.Decode(Wire.Read(client)),"type","")!="ready")throw new Exception("Ready handshake");
                }
            }
            passed.Add("PASS: production local connection exchanges state and ready on an isolated Windows pipe");
            byte[] output=Encoding.UTF8.GetBytes(String.Join(Environment.NewLine,passed)+Environment.NewLine);
            Console.OpenStandardOutput().Write(output,0,output.Length); return 0;
        } catch(Exception error) { byte[] output=Encoding.UTF8.GetBytes("FAIL: "+error.Message+Environment.NewLine); Console.OpenStandardOutput().Write(output,0,output.Length); return 1; }
    }
}

internal sealed class LocalConnection : IDisposable {
    internal readonly string Name;
    internal LocalConnection() : this(Program.PipeName) {}
    internal LocalConnection(string name) {Name=name;}
    private NamedPipeServerStream server;
    private volatile bool disposed;
    private readonly object writeLock=new object();
    internal event Action<Dictionary<string,object>> Received;
    internal event Action<bool> Connected;
    internal void Start() { var thread=new Thread(Listen){IsBackground=true}; thread.Start(); }
    internal static PipeSecurity CreatePipeSecurity() {
        var security=new PipeSecurity();
        security.SetAccessRuleProtection(true,false);
        // Reject network logons even when they authenticate as the same user.
        var networkSid=new SecurityIdentifier(WellKnownSidType.NetworkSid,null);
        security.AddAccessRule(new PipeAccessRule(networkSid,PipeAccessRights.FullControl,AccessControlType.Deny));
        using(var user=WindowsIdentity.GetCurrent()) {
            security.AddAccessRule(new PipeAccessRule(user.User,PipeAccessRights.FullControl,AccessControlType.Allow));
        }
        return security;
    }
    private void Listen() {
        while(!disposed) {
            try {
                using(var current=new NamedPipeServerStream(Name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,CreatePipeSecurity())) {
                    lock(writeLock) server=current;
                    current.WaitForConnection(); if(disposed) return;
                    if(Connected!=null) Connected(true);
                    string text;
                    while(!disposed && (text=Wire.Read(current))!=null) {
                        var data=Wire.Decode(text); string type=Wire.Text(data,"type","");
                        if((type=="state" || type=="error") && Received!=null) Received(data);
                    }
                }
            } catch { if(disposed) return; }
            finally { lock(writeLock) server=null; if(Connected!=null) Connected(false); }
            if(!disposed) Thread.Sleep(250);
        }
    }
    internal bool Send(object data) {
        lock(writeLock) {
            try { if(server==null || !server.IsConnected) return false; Wire.Write(server,Wire.Encode(data)); return true; }
            catch { return false; }
        }
    }
    public void Dispose() { disposed=true; lock(writeLock) { if(server!=null) server.Dispose(); } }
}

internal sealed class ReinerForm : Form {
    private const int MouseActivateMessage = 0x0021;
    private static readonly IntPtr KeepActiveWindow = new IntPtr(3); // MA_NOACTIVATE
    private Point down,origin;
    private bool dragging,moved;
    private readonly LocalConnection connection=new LocalConnection();
    private ControlsForm controls;
    private Image mascot;
    private readonly ToolTip tip=new ToolTip();
    private readonly bool startConnection;
    internal bool IsConnected;
    internal Dictionary<string,object> State;
    internal string Status="Right-click Reiner and choose Connect.";
    protected override bool ShowWithoutActivation { get { return true; } }
    internal ReinerForm() : this(true) {}
    internal ReinerForm(bool connect) {
        startConnection=connect; Text="Cozy Desktop — Reiner"; FormBorderStyle=FormBorderStyle.None;
        TopMost=true; ShowInTaskbar=false; StartPosition=FormStartPosition.Manual; Size=new Size(42,42);
        // Per-pixel alpha avoids magenta color-key fringes around translucent artwork.
        BackColor=Color.Black;
        using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Reiner.png")) mascot=new Bitmap(stream);
        var area=Screen.PrimaryScreen.WorkingArea; Location=new Point(area.Right-85,area.Bottom-250);
        Cursor=Cursors.Hand; tip.SetToolTip(this,"Click for controls · Drag to move · Right-click for setup or exit");
        var menu=new ContextMenuStrip();
        menu.Items.Add("Playback controls",null,(s,e)=>ToggleControls());
        menu.Items.Add("Connect",null,(s,e)=>ShowSetup());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Cozy Desktop",null,(s,e)=>Close()); ContextMenuStrip=menu;
        MouseDown+=(s,e)=>{ if(e.Button!=MouseButtons.Left) return; down=MousePosition; origin=Location; dragging=true; moved=false; Capture=true; };
        MouseMove+=(s,e)=>{
            if(!dragging) return; Point delta=new Point(MousePosition.X-down.X,MousePosition.Y-down.Y);
            if(Math.Abs(delta.X)>4 || Math.Abs(delta.Y)>4) moved=true;
            if(moved) { var bounds=Screen.FromPoint(MousePosition).WorkingArea;
                Location=new Point(Math.Max(bounds.Left,Math.Min(bounds.Right-Width,origin.X+delta.X)),Math.Max(bounds.Top,Math.Min(bounds.Bottom-Height,origin.Y+delta.Y)));
                AnchorPanel(); }
        };
        MouseUp+=(s,e)=>{ if(e.Button!=MouseButtons.Left || !dragging)return; dragging=false; Capture=false; if(!moved)ToggleControls(); };
        connection.Connected+=connected=>OnUi(()=>{IsConnected=connected; if(connected)connection.Send(new {type="ready"});else{State=null;Status="Disconnected. Right-click Reiner and choose Connect.";}RefreshControls();});
        connection.Received+=data=>OnUi(()=>{
            if(Wire.Text(data,"type","")=="error") Status=Wire.Text(data,"message","Playback control unavailable.");
            else {State=data;Status=Wire.Bool(data,"available")?"":"Open a YouTube watch video. Keep its tab open.";}
            RefreshControls();
        });
        Shown+=(s,e)=>{
            RenderMascot(); if(!startConnection)return; connection.Start();
            string hostManifest=Path.Combine(Program.Home,"Cozy.NativeHost.json");
            if(!File.Exists(hostManifest)) ShowSetup();
        };
        FormClosed+=(s,e)=>{connection.Dispose(); if(controls!=null)controls.Dispose(); mascot.Dispose(); tip.Dispose();};
    }
    private void OnUi(Action action) { if(IsDisposed || !IsHandleCreated)return; try{BeginInvoke(action);}catch(InvalidOperationException){} }
    protected override CreateParams CreateParams { get {var value=base.CreateParams;value.ExStyle|=0x80000;return value;} }
    protected override void WndProc(ref Message message) {
        // Keep the panel active until MouseUp toggles it. Dragging Reiner also keeps it open.
        if(message.Msg==MouseActivateMessage && controls!=null && controls.Visible) {
            message.Result=KeepActiveWindow;
            return;
        }
        base.WndProc(ref message);
    }
    protected override void OnPaint(PaintEventArgs e) { }
    private void RenderMascot() { AlphaWindow.Apply(this,mascot); }
    private void RefreshControls() {
        if(controls==null || controls.IsDisposed)return;
        int previousHeight=controls.Height;
        controls.UpdateState();
        if(controls.Height!=previousHeight)AnchorPanel();
    }
    internal string MediaKey { get { return Wire.Text(State,"key",""); } }
    internal void Send(string action,double? value) { Send(action,value,MediaKey); }
    internal void Send(string action,double? value,string key) {
        var message=new Dictionary<string,object>{{"type","command"},{"action",action},{"key",key}};
        if(value.HasValue)message["value"]=value.Value;
        if(!Wire.Command(message) || !connection.Send(message)) {Status="Right-click Reiner and choose Connect.";RefreshControls();}
    }
    private void ToggleControls() {
        if(controls==null || controls.IsDisposed)controls=new ControlsForm(this);
        if(controls.Visible){controls.Hide();return;}
        controls.UpdateState(); AnchorPanel(); controls.Show(this); controls.Activate();
        connection.Send(new {type="refresh"});
    }
    private void AnchorPanel() {
        if(controls==null || controls.IsDisposed)return;
        Rectangle area=Screen.FromControl(this).WorkingArea;
        int x=Math.Max(area.Left,Math.Min(area.Right-controls.Width,Left));
        int y=Bottom+3;
        if(y+controls.Height>area.Bottom)y=Top-controls.Height-3;
        controls.Location=new Point(x,Math.Max(area.Top,y));
    }
    internal void ShowSetup() { using(var setup=new SetupForm())setup.ShowDialog(this); }
}

internal sealed class CozySlider : Control {
    internal double Minimum=0,Maximum=1;
    internal double Value;
    internal event Action<double> Changed;
    internal event Action Started;
    internal bool Editing;
    internal CozySlider() { DoubleBuffered=true; TabStop=true; Height=22; AccessibleRole=AccessibleRole.Slider; Cursor=Cursors.Hand; }
    internal void Set(double value) { if(!Editing){Value=Math.Max(Minimum,Math.Min(Maximum,value));Invalidate();} }
    private void Input(int x) {
        if(!Enabled)return; Value=Minimum+(Maximum-Minimum)*Math.Max(0,Math.Min(1,(double)(x-5)/Math.Max(1,Width-10)));
        Invalidate(); if(Changed!=null)Changed(Value);
    }
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left && Enabled){Focus();Editing=true;if(Started!=null)Started();Capture=true;Input(e.X);}}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(Editing)Input(e.X);}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(Editing){Input(e.X);Editing=false;Capture=false;}}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)Editing=false;}
    protected override bool IsInputKey(Keys key){return key==Keys.Left||key==Keys.Right||base.IsInputKey(key);}
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){if(Started!=null)Started();Value=Math.Max(Minimum,Math.Min(Maximum,Value+(e.KeyCode==Keys.Left?-1:1)*(Maximum-Minimum)/100));Invalidate();if(Changed!=null)Changed(Value);e.Handled=true;}}
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias; float y=Height/2f;
        float part=(float)((Value-Minimum)/Math.Max(.0001,Maximum-Minimum)); float x=5+part*(Width-10);
        using(var pen=new Pen(Color.FromArgb(190,174,155),4))e.Graphics.DrawLine(pen,5,y,Width-5,y);
        using(var pen=new Pen(Enabled?Color.FromArgb(117,44,44):Color.Gray,4))e.Graphics.DrawLine(pen,5,y,x,y);
        using(var brush=new SolidBrush(Enabled?Color.FromArgb(117,44,44):Color.Gray))e.Graphics.FillEllipse(brush,x-5,y-5,10,10);
        if(Focused)using(var pen=new Pen(Color.FromArgb(150,110,75)))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
    }
}

internal sealed class ControlsForm : Form {
    private readonly ReinerForm owner;
    private readonly Label title,time,status;
    private readonly Button play,previous,next,back,forward,mute;
    private readonly Button connect;
    private readonly CozySlider seek,volume;
    private readonly ComboBox speed;
    private bool updating;
    internal ControlsForm(ReinerForm parent) {
        owner=parent; Text="Cozy Desktop playback controls"; Size=new Size(246,140); FormBorderStyle=FormBorderStyle.None;
        TopMost=true;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual; BackColor=Color.FromArgb(234,216,189);
        Font=new Font("Segoe UI",9); ForeColor=Color.FromArgb(48,34,28); KeyPreview=true;
        title=new Label {Left=10,Top=8,Width=226,Height=20,AutoEllipsis=true};Controls.Add(title);
        connect=ButtonAt("Connect",161,8,74,"Connect desktop controls",()=>owner.ShowSetup());
        previous=ButtonAt("|◀",10,31,42,"Previous video",()=>owner.Send("previous",null));
        back=ButtonAt("−10",56,31,42,"Back 10 seconds",()=>owner.Send("skip",-10));
        play=ButtonAt("▶",102,31,42,"Play or pause",()=>owner.Send("play",null));
        play.BackColor=Color.FromArgb(117,44,44);play.ForeColor=Color.White;
        forward=ButtonAt("+10",148,31,42,"Forward 10 seconds",()=>owner.Send("skip",10));
        next=ButtonAt("▶|",194,31,42,"Next video",()=>owner.Send("next",null));
        string seekKey="",volumeKey="";
        seek=new CozySlider {Left=10,Top=61,Width=226,AccessibleName="Seek"};seek.Started+=()=>seekKey=owner.MediaKey;seek.Changed+=value=>owner.Send("seek",value,seekKey);Controls.Add(seek);
        time=new Label {Left=10,Top=82,Width=226,Height=15,TextAlign=ContentAlignment.MiddleRight,Font=new Font("Segoe UI",8)};Controls.Add(time);
        mute=ButtonAt("Mute",10,100,52,"Mute or unmute",()=>owner.Send("mute",null));
        volume=new CozySlider {Left=68,Top=104,Width=48,AccessibleName="Volume"};volume.Started+=()=>volumeKey=owner.MediaKey;volume.Changed+=value=>owner.Send("volume",Math.Round(value,2),volumeKey);Controls.Add(volume);
        speed=new ComboBox {Left=173,Top=103,Width=63,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Playback speed"};
        foreach(string rate in new[]{"0.25×","0.5×","0.75×","1×","1.25×","1.5×","1.75×","2×"})speed.Items.Add(rate);
        speed.SelectedIndex=3;speed.SelectedIndexChanged+=(s,e)=>{if(!updating)owner.Send("speed",new double[]{.25,.5,.75,1,1.25,1.5,1.75,2}[speed.SelectedIndex]);};Controls.Add(speed);
        status=new Label {Left=10,Top=136,Width=226,Height=27,Font=new Font("Segoe UI",8),ForeColor=Color.FromArgb(117,44,44)};Controls.Add(status);
        KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Hide();e.Handled=true;}};
        SizeChanged+=(s,e)=>SetRoundRegion();
        SetRoundRegion();
    }
    private Button ButtonAt(string text,int x,int y,int width,string accessible,Action click) {
        var button=new Button {Text=text,Left=x,Top=y,Width=width,Height=29,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(255,250,241),AccessibleName=accessible,TabStop=true};
        button.FlatAppearance.BorderColor=Color.FromArgb(185,149,121);button.Click+=(s,e)=>click();Controls.Add(button);return button;
    }
    protected override void OnDeactivate(EventArgs e) {
        base.OnDeactivate(e);
        if(Visible)Hide();
    }
    private void SetRoundRegion() {
        using(var path=new GraphicsPath()) { int r=28;path.AddArc(0,0,r,r,180,90);path.AddArc(Width-r,0,r,r,270,90);path.AddArc(Width-r,Height-r,r,r,0,90);path.AddArc(0,Height-r,r,r,90,90);path.CloseFigure();Region=new Region(path); }
    }
    internal void UpdateState() {
        var data=owner.State;bool available=owner.IsConnected && Wire.Bool(data,"available"); updating=true;
        connect.Visible=!owner.IsConnected;
        title.Width=owner.IsConnected?226:146;
        title.Text=Wire.Text(data,"title",owner.IsConnected?"Open a YouTube video":"Desktop controls");
        title.Top=available?8:15;
        play.Visible=previous.Visible=next.Visible=back.Visible=forward.Visible=mute.Visible=seek.Visible=volume.Visible=speed.Visible=time.Visible=available;
        play.Enabled=mute.Enabled=volume.Enabled=speed.Enabled=available;
        previous.Enabled=available&&Wire.Bool(data,"canPrevious");next.Enabled=available&&Wire.Bool(data,"canNext");
        seek.Enabled=back.Enabled=forward.Enabled=available&&Wire.Bool(data,"canSeek");
        play.Text=Wire.Bool(data,"paused")?"▶":"Ⅱ";mute.Text=Wire.Bool(data,"muted")?"Unmute":"Mute";
        volume.Set(Wire.Number(data,"volume",1));
        if(!seek.Editing){seek.Minimum=Wire.Number(data,"start",0);seek.Maximum=Math.Max(seek.Minimum+.1,Wire.Number(data,"end",1));seek.Set(Wire.Number(data,"time",0));}
        if(!speed.DroppedDown) {double rate=Wire.Number(data,"rate",1);int index=Array.IndexOf(new double[]{.25,.5,.75,1,1.25,1.5,1.75,2},rate);if(index>=0)speed.SelectedIndex=index;}
        time.Text=FormatTime(Wire.Number(data,"time",0))+" / "+(data!=null&&data.ContainsKey("duration")&&data["duration"]==null?"LIVE":FormatTime(Wire.Number(data,"duration",0)));
        status.Text=owner.Status;
        bool hasStatus=available && !String.IsNullOrEmpty(owner.Status);
        status.Visible=hasStatus;
        Height=available?(hasStatus?168:140):46;
        updating=false;
    }
    private string FormatTime(double seconds){var span=TimeSpan.FromSeconds(Math.Max(0,Math.Min(1e8,seconds)));return seconds>=3600?String.Format("{0}:{1:00}:{2:00}",(int)span.TotalHours,span.Minutes,span.Seconds):String.Format("{0}:{1:00}",(int)span.TotalMinutes,span.Seconds);}
}


internal sealed class SetupForm : Form {
 private readonly ComboBox browser;
 private readonly Label status;
 private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
 internal SetupForm() {
  Text="Cozy Desktop";ClientSize=new Size(258,111);FormBorderStyle=FormBorderStyle.FixedDialog;
  MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterScreen;TopMost=true;
  BackColor=Color.FromArgb(234,216,189);Font=new Font("Segoe UI",9);
  browser=new ComboBox {Left=12,Top=16,Width=105,AccessibleName="Browser",DropDownStyle=ComboBoxStyle.DropDownList};browser.Items.AddRange(new object[]{"Brave","Chrome","Edge"});browser.SelectedIndex=0;Controls.Add(browser);
  var connect=new Button {Text="Connect",Left=129,Top=12,Width=116,Height=32,BackColor=Color.FromArgb(117,44,44),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};Controls.Add(connect);AcceptButton=connect;
  status=new Label {Left=12,Top=50,Width=234,Height=36,Text="Leave the connection tab open."};Controls.Add(status);
  connect.Click+=(s,e)=>{
   try {
    string name=(string)browser.SelectedItem;
    Program.Register(name);
    Program.OpenConnectionPage(name);
    status.Text="Connecting to "+name+"...";
   } catch {status.Text="Open the extension menu in your browser and press Connect.";}
  };
  var remove=new LinkLabel {Left=12,Top=88,Width=120,Height=18,Text="Remove connection"};Controls.Add(remove);
  remove.LinkClicked+=(s,e)=>{try{Program.Unregister((string)browser.SelectedItem);status.Text="Connection removed. Restart the browser to end an existing link.";}catch{status.Text="Could not remove connection.";}};
  timer.Interval=300;timer.Tick+=(s,e)=>{var icon=Owner as ReinerForm;if(icon!=null && icon.IsConnected){timer.Stop();Close();}};timer.Start();
  FormClosed+=(s,e)=>timer.Dispose();
 }
}

internal static class AlphaWindow {
 [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;public Point(int x,int y){X=x;Y=y;}}
 [StructLayout(LayoutKind.Sequential)] private struct Size {public int X,Y;public Size(int x,int y){X=x;Y=y;}}
 [StructLayout(LayoutKind.Sequential,Pack=1)] private struct Blend {public byte Op,Flags,Alpha,Format;}
 [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
 [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
 [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
 [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
 [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
 [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
 [DllImport("user32.dll",SetLastError=true)] private static extern bool UpdateLayeredWindow(IntPtr window,IntPtr dc,ref Point dest,ref Size size,IntPtr source,ref Point origin,int key,ref Blend blend,int flags);
 internal static Bitmap Render(Image image) {
  var bitmap=new Bitmap(42,42,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
  using(var graphics=Graphics.FromImage(bitmap)){graphics.Clear(Color.Transparent);graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.DrawImage(image,new Rectangle(4,4,34,34));}
  return bitmap;
 }
 internal static void Apply(Form window,Image image) {
  using(var bitmap=Render(image)){
   IntPtr screen=GetDC(IntPtr.Zero),memory=IntPtr.Zero,handle=IntPtr.Zero,old=IntPtr.Zero;
   try {memory=CreateCompatibleDC(screen);handle=bitmap.GetHbitmap(Color.FromArgb(0));old=SelectObject(memory,handle);
    var dest=new Point(window.Left,window.Top);var origin=new Point(0,0);var size=new Size(bitmap.Width,bitmap.Height);var blend=new Blend {Op=0,Flags=0,Alpha=255,Format=1};
    if(!UpdateLayeredWindow(window.Handle,screen,ref dest,ref size,memory,ref origin,0,ref blend,2))throw new System.ComponentModel.Win32Exception();
   }finally{if(old!=IntPtr.Zero)SelectObject(memory,old);if(handle!=IntPtr.Zero)DeleteObject(handle);if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);}
  }
 }
}
