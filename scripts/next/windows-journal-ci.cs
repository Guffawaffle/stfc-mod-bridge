// Fixed test-gate context bootstrap. No credentials, borrowed token, game target,
// arbitrary command/path/PID/token selector, or privilege-installation route.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace StfcBridgeJournalCi {
    public sealed class CiFailure : Exception {
        public string Code { get; private set; }
        public int NativeError { get; private set; }
        public CiFailure(string code, int nativeError = 0) : base(code) { Code = code; NativeError = nativeError; }
    }
    public sealed class Context {
        public string ObservationKind { get; set; }
        public uint Pid { get; set; }
        public string CreationFiletime { get; set; }
        public uint TokenType { get; set; }
        public bool Elevated { get; set; }
        public uint ElevationType { get; set; }
        public uint IntegrityRid { get; set; }
        public uint IntegrityAttributes { get; set; }
        public bool ThreadTokenAbsent { get; set; }
        public ushort ProcessMachine { get; set; }
        public ushort NativeMachine { get; set; }
        public string UserSid { get; set; }
        public uint SessionId { get; set; }
        public string AuthenticationId { get; set; }
        public string LocalAppData { get; set; }
        public bool AdministratorsPresent { get; set; }
        public uint AdministratorsAttributes { get; set; }
        public bool NonTraversalPrivilegeEnabled { get; set; }
    }
    public sealed class FileObservation {
        public string Role { get; set; }
        public string Route { get; set; }
        public long Bytes { get; set; }
        public string Sha256 { get; set; }
        public string FileIdentity { get; set; }
    }
    public sealed class ProbeObservation {
        public string SchemaVersion { get; set; } = "bridge-windows-journal-ci-token-probe/v1";
        public Context SourceContext { get; set; }
        public Context DerivedContext { get; set; }
        public bool DerivedAttempted { get; set; }
        public bool DerivedPredicateAccepted { get; set; }
        public StartupObjectObservation[] StartupObjects { get; set; }
        public string FailureCode { get; set; }
        public int NativeError { get; set; }
        public bool ChildLaunched { get; set; } = false;
        public bool HostedCiProved { get; set; } = false;
    }
    public sealed class SecuritySnapshot {
        public uint RequestedInformation { get; set; }
        public uint RequiredBytes { get; set; }
        public string Result { get; set; } = "unavailable";
        public string DescriptorBase64 { get; set; }
        public string Sha256 { get; set; }
        public string FailureCode { get; set; }
        public int NativeError { get; set; }
        internal byte[] Raw;
    }
    public sealed class StartupObjectObservation {
        public string ObservationKind { get; set; } = "parent_current_user_object";
        public string Role { get; set; }
        public string Name { get; set; }
        public uint Flags { get; set; }
        public string SelectedTokenObservationKind { get; set; }
        public uint AccessCheckTokenType { get; set; }
        public SecuritySnapshot OwnerGroupDacl { get; set; }
        public SecuritySnapshot MandatoryLabel { get; set; }
        public string MappingKind { get; set; }
        public uint DesiredAccess { get; set; } = 0x02000000;
        public bool DaclCheckApiSucceeded { get; set; }
        public bool? DaclAccessStatus { get; set; }
        public uint? DaclGrantedAccess { get; set; }
        public string PrivilegeSetBase64 { get; set; }
        public bool DaclOnly { get { return true; } }
        public bool ChildObjectAssignmentObserved { get { return false; } }
        public bool DesktopSelected { get { return false; } }
        public string FailureCode { get; set; }
        public int NativeError { get; set; }
    }
    public sealed class DesktopContext {
        public string ObservationKind { get; set; } = "own_process_window_station_and_current_thread_desktop";
        public string WindowStationName { get; set; }
        public string ThreadDesktopName { get; set; }
    }
    public class StartupSecurityPayload {
        public string Result { get; set; } = "unavailable";
        public string AclState { get; set; } = "unavailable";
        public uint Bytes { get; set; }
        public string DataBase64 { get; set; }
        public string Sha256 { get; set; }
        public string FailureCode { get; set; }
        public uint NativeError { get; set; }
    }
    public sealed class TokenDefaultDaclObservation : StartupSecurityPayload {
        public string ObservationKind { get; set; } = "selected_primary_token_default_dacl";
        public string SelectedTokenObservationKind { get; set; }
        public uint TokenInformationClass { get; set; } = 6;
        public uint ReturnedBytes { get; set; }
    }
    public sealed class OwnedChildSecurityObservation : StartupSecurityPayload {
        public string ObservationKind { get; set; } = "owned_child_kernel_object_security";
        public string Role { get; set; }
        public string Phase { get; set; } = "assigned_before_resume";
        public uint Pid { get; set; }
        public string CreationFiletime { get; set; }
        public uint InitialThreadId { get; set; }
        public uint ObjectType { get; set; } = 6;
        public uint RequestedInformation { get; set; } = 7;
    }
    public sealed class StartupSecurityObservation {
        public string SchemaVersion { get; set; } = "bridge-windows-journal-ci-startup-security/v1";
        public TokenDefaultDaclObservation SelectedTokenDefaultDacl { get; set; }
        public OwnedChildSecurityObservation[] OwnedChildObjects { get; set; }
    }
    public sealed class LaunchObservation {
        public string SchemaVersion { get; set; } = "bridge-windows-journal-ci-launch/v1";
        public string Result { get; set; } = "failed";
        public string ArtifactId { get; set; }
        public string ReceiptPath { get; set; }
        public string StartedAt { get; set; } = DateTime.UtcNow.ToString("O");
        public string CompletedAt { get; set; }
        public string PowerShellVersion { get; set; }
        public string LaunchRoute { get; set; }
        public string DotnetVersion { get; set; } = Environment.Version.ToString();
        public ProbeObservation TokenProbe { get; set; }
        public StartupObjectObservation[] StartupObjects { get; set; }
        public string RequestedDesktop { get; set; }
        public DesktopContext ChildDesktopContext { get; set; }
        public bool ChildDesktopContextMatched { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public StartupSecurityObservation StartupSecurity { get; set; }
        public FileObservation[] Files { get; set; }
        public uint ChildPid { get; set; }
        public string ChildCreationFiletime { get; set; }
        public bool AssignedBeforeResume { get; set; }
        public bool ChildContextMatched { get; set; }
        public bool ExitObserved { get; set; }
        public uint? ChildExitCode { get; set; }
        public bool StdoutEof { get; set; }
        public bool StderrEof { get; set; }
        public bool ReaderFailed { get; set; }
        public bool OutputOverflow { get; set; }
        public bool OwnedJobEmpty { get; set; }
        public bool ForcedCleanup { get; set; }
        public bool CleanupKillApiSucceeded { get; set; }
        public bool CleanupSettled { get; set; }
        public bool IoThreadsJoined { get; set; }
        public bool SourceToolFenceStable { get; set; }
        public string FailureCode { get; set; }
        public int NativeError { get; set; }
        public bool NodeTokenSelfObserved { get; set; } = false;
        public bool Native9Observed { get; set; } = false;
        public bool HostedCiProved { get; set; } = false;
        public bool PackageAcceptance { get { return false; } }
        public bool FullBr06Accepted { get { return false; } }
        public bool NativeRuntimeQualified { get { return false; } }
        public bool ReleaseQualified { get { return false; } }
    }
    internal sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid {
        internal Handle() : base(true) { }
        internal Handle(IntPtr value) : base(true) { SetHandle(value); }
        internal void Attach(IntPtr value) { SetHandle(value); }
        protected override bool ReleaseHandle() { return Native.CloseHandle(handle); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SidAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct TokenStatistics {
        public Luid TokenId, AuthenticationId; public long Expiration;
        public uint Type, Impersonation, DynamicCharged, DynamicAvailable, Groups, Privileges; public Luid ModifiedId;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low, High; public ulong Value { get { return ((ulong)High << 32) | Low; } } }
    [StructLayout(LayoutKind.Sequential)] internal struct FileInformation {
        public uint Attributes; public FileTime Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] internal struct GenericMapping { public uint Read, Write, Execute, All; }
    [StructLayout(LayoutKind.Sequential)] internal struct UserObjectFlags { public int Inherit, Reserved; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits {
        public long PeriodTime, UserTime; public uint Flags; public UIntPtr MinWorking, MaxWorking;
        public uint ActiveLimit; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits {
        public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Accounting {
        public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, Total, Active, Terminated;
    }
    internal static class Native {
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetProcessTimes(IntPtr p, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool IsWow64Process2(IntPtr p, out ushort machine, out ushort native);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool OpenProcessToken(IntPtr p, uint rights, out Handle token);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool OpenThreadToken(IntPtr t, uint rights, bool asSelf, out Handle token);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool GetTokenInformation(Handle token, int type, IntPtr data, uint capacity, out uint returned);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool CreateWellKnownSid(int type, IntPtr domain, IntPtr sid, ref uint capacity);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool CreateRestrictedToken(Handle source, uint flags, uint disableCount, ref SidAttributes disable, uint deleteCount, IntPtr deleted, uint restrictCount, IntPtr restricted, out Handle result);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool DuplicateTokenEx(Handle source, uint rights, IntPtr attributes, int level, int type, out Handle duplicate);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool AccessCheck(IntPtr descriptor, Handle token, uint desired, ref GenericMapping mapping, IntPtr privileges, ref uint privilegeBytes, out uint granted, out bool accessStatus);
        [DllImport("advapi32.dll")] internal static extern bool IsValidSecurityDescriptor(IntPtr descriptor);
        [DllImport("advapi32.dll")] internal static extern bool IsValidAcl(IntPtr acl);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(IntPtr handle, uint objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr allocation);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
        [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr data, uint bytes, out uint needed);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information, IntPtr descriptor, uint bytes, out uint needed);
        [DllImport("advapi32.dll", SetLastError=true)] internal static extern bool SetTokenInformation(Handle token, int type, ref SidAttributes value, uint bytes);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] internal static extern int SHGetKnownFolderPath(ref Guid id, uint flags, Handle token, out IntPtr path);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern uint GetModuleFileName(IntPtr module, StringBuilder value, uint capacity);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder value, uint capacity, uint flags);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool CreateDirectory(string path, IntPtr security);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CreatePipe(out Handle read, out Handle write, ref SecurityAttributes sa, uint size);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetHandleInformation(Handle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool DuplicateHandle(IntPtr source, IntPtr value, IntPtr destination, out Handle copy, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr GetStdHandle(int kind);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, uint count, uint flags, ref UIntPtr size);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr key, IntPtr value, UIntPtr bytes, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool CreateProcessAsUser(Handle token, string application, StringBuilder command, IntPtr processSa, IntPtr threadSa, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSa, IntPtr threadSa, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern uint ResumeThread(Handle thread);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern uint WaitForSingleObject(Handle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetExitCodeProcess(Handle handle, out uint code);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool TerminateProcess(Handle process, uint code);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern Handle CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetInformationJobObject(Handle job, int type, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool AssignProcessToJobObject(Handle job, Handle process);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool QueryInformationJobObject(Handle job, int type, out Accounting accounting, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool TerminateJobObject(Handle job, uint code);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CancelSynchronousIo(Handle thread);
    }
    internal sealed class Buffer : IDisposable {
        internal IntPtr Pointer; internal int Length;
        internal Buffer(int bytes) { Length=bytes; Pointer=Marshal.AllocHGlobal(bytes); for(int i=0;i<bytes;i++) Marshal.WriteByte(Pointer,i,0); }
        public void Dispose() { if(Pointer!=IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer=IntPtr.Zero; } }
    }
    internal sealed class FileFence : IDisposable {
        internal FileObservation Observation; private FileStream stream;
        internal FileFence(string role, string route, long cap) {
            WindowsJournalCi.NoReparse(route, false);
            stream=new FileStream(route,FileMode.Open,FileAccess.Read,FileShare.Read);
            try { Observation=Read(role,route,cap); } catch { stream.Dispose(); throw; }
        }
        private FileObservation Read(string role,string route,long cap) {
            FileInformation info; WindowsJournalCi.Check(Native.GetFileInformationByHandle(stream.SafeFileHandle,out info),"FILE_INFORMATION");
            WindowsJournalCi.Require(stream.Length>0&&stream.Length<=cap,"FILE_BOUND");
            WindowsJournalCi.Require(WindowsJournalCi.SamePath(WindowsJournalCi.FinalPath(stream.SafeFileHandle),route),"FILE_ROUTE");
            stream.Position=0; byte[] hash; using(var sha=SHA256.Create()) hash=sha.ComputeHash(stream);
            return new FileObservation { Role=role,Route=Path.GetFullPath(route),Bytes=stream.Length,Sha256=Convert.ToHexString(hash).ToLowerInvariant(),
                FileIdentity=info.Volume.ToString("x8")+":"+info.IndexHigh.ToString("x8")+info.IndexLow.ToString("x8")+":"+info.Written.Value.ToString() };
        }
        internal void Verify() { WindowsJournalCi.NoReparse(Observation.Route,false); var current=Read(Observation.Role,Observation.Route,Observation.Bytes);
            WindowsJournalCi.Require(current.Bytes==Observation.Bytes&&current.Sha256==Observation.Sha256&&current.FileIdentity==Observation.FileIdentity,"FILE_FENCE_CHANGED"); }
        public void Dispose() { if(stream!=null) { stream.Dispose(); stream=null; } }
    }
    internal sealed class CapturedPipe {
        internal volatile bool Eof, Failed; internal Thread Reader; internal Handle ReaderThread;
        private bool started;
        private readonly Handle pipe; private readonly string path; private readonly Action<int> account;
        internal CapturedPipe(Handle handle,string route,Action<int> count) { pipe=handle; path=route; account=count; }
        internal void Start() {
            Reader=new Thread(()=> { try {
                Handle own; WindowsJournalCi.Check(Native.DuplicateHandle(Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcess(),out own,0,false,2),"READER_THREAD"); ReaderThread=own;
                using(var safe=new SafeFileHandle(pipe.DangerousGetHandle(),false)) using(var input=new FileStream(safe,FileAccess.Read,65536,false))
                using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {
                    byte[] block=new byte[65536]; int count;
                    while((count=input.Read(block,0,block.Length))!=0) { account(count); output.Write(block,0,count); }
                    output.Flush(true); Eof=true;
                }
            } catch { Failed=true; } finally { pipe.Dispose(); if(ReaderThread!=null) ReaderThread.Dispose(); } });
            Reader.IsBackground=true; Reader.Start(); started=true;
        }
        internal void Cancel() { try { var owned=ReaderThread; if(owned!=null&&!owned.IsClosed) Native.CancelSynchronousIo(owned); } catch(ObjectDisposedException) {} }
        internal bool Join(int milliseconds) { return started&&Reader.Join(milliseconds); }
        internal void CloseUnstarted() { if(!started) pipe.Dispose(); }
    }
    internal sealed class HandshakeWriter {
        internal volatile bool Completed, Failed; private Thread writer; private Handle writerThread; private readonly Handle pipe; private bool started;
        internal HandshakeWriter(Handle handle) { pipe=handle; }
        internal void Start(byte[] bytes) {
            writer=new Thread(()=> { try { Handle own; WindowsJournalCi.Check(Native.DuplicateHandle(Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcess(),out own,0,false,2),"WRITER_THREAD"); writerThread=own;
                using(var safe=new SafeFileHandle(pipe.DangerousGetHandle(),false)) using(var stream=new FileStream(safe,FileAccess.Write,4096,false)) stream.Write(bytes,0,bytes.Length);
                Completed=true;
            } catch { Failed=true; } finally { pipe.Dispose(); if(writerThread!=null) writerThread.Dispose(); } });
            writer.IsBackground=true; writer.Start(); started=true;
        }
        internal void Cancel() { try { var owned=writerThread; if(owned!=null&&!owned.IsClosed) Native.CancelSynchronousIo(owned); } catch(ObjectDisposedException) {} }
        internal bool Join(int milliseconds) { return started&&writer.Join(milliseconds); }
        internal void CloseUnstarted() { if(!started) pipe.Dispose(); }
    }
    internal sealed class CreatedProcess {
        // Both wrappers exist before the native create call. Returned handles enter
        // custody before validation, allocations or other fallible operations.
        internal readonly Handle Process=new Handle(); internal readonly Handle Thread=new Handle(); internal uint Pid, Tid;
    }
    internal sealed class OutputBudget {
        private long bytes; internal volatile bool Overflow;
        internal void Add(int count) { WindowsJournalCi.Require(count>0&&count<=65536,"CAPTURE_BLOCK_BOUND");
            if(Interlocked.Add(ref bytes,count)>64L*1024*1024) { Overflow=true; throw new CiFailure("OUTPUT_BOUND"); } }
    }
    public static class WindowsJournalCi {
        private const uint Query=0x8, Duplicate=0x2, Assign=0x1, AdjustDefault=0x80, Impersonate=0x4;
        private const int ChildMs=650000, WorkMs=670000, TotalMs=700000, CleanupMs=30000, ReaderGraceMs=500;
        private const int SecurityBytes=4096, SecurityDiagnosticBytes=24576, ReceiptBytes=65536;
        private static readonly JsonSerializerOptions JsonOptions=new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,MaxDepth=24 };
        private static readonly string[] SourceNames={"windows-journal-ci.ps1","windows-journal-ci.cs","windows-journal-ci-child.ps1","windows-journal-ci-entry.mjs","windows-journal-ci-entry-policy.mjs"};
        internal static void Require(bool value,string code) { if(!value) throw new CiFailure(code); }
        internal static void Check(bool value,string code) { if(!value) throw new CiFailure(code,Marshal.GetLastWin32Error()); }
        private static int Remaining(Stopwatch timer,long deadline) { return (int)Math.Max(0,Math.Min(Int32.MaxValue,deadline-timer.ElapsedMilliseconds)); }
        private static long CleanupDeadline(long start) { return Math.Min(TotalMs,start+CleanupMs); }
        private static bool ReaderCancellationDue(long elapsed,long start,long end) { return elapsed>=end||elapsed-start>=ReaderGraceMs; }
        internal static bool SamePath(string a,string b) { return String.Equals(Path.GetFullPath(a).TrimEnd('\\'),Path.GetFullPath(b).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase); }
        internal static string FinalPath(SafeFileHandle handle) {
            var value=new StringBuilder(32768); uint count=Native.GetFinalPathNameByHandle(handle,value,(uint)value.Capacity,0);
            Require(count>0&&count<value.Capacity,"CANONICAL_PATH_BOUND"); string path=value.ToString();
            Require(path.StartsWith("\\\\?\\",StringComparison.Ordinal)&&!path.StartsWith("\\\\?\\UNC\\",StringComparison.OrdinalIgnoreCase),"LOCAL_DRIVE_REQUIRED"); return path.Substring(4);
        }
        internal static void NoReparse(string path,bool directory) {
            string full=Path.GetFullPath(path), root=Path.GetPathRoot(full); Require(root.Length==3&&root[1]==':',"LOCAL_DRIVE_REQUIRED");
            string current=root; var parts=full.Substring(root.Length).Split('\\',StringSplitOptions.RemoveEmptyEntries);
            foreach(string part in parts) { current=Path.Combine(current,part); var attr=File.GetAttributes(current); Require((attr&FileAttributes.ReparsePoint)==0,"REPARSE_ROUTE"); }
            Require(((File.GetAttributes(full)&FileAttributes.Directory)!=0)==directory,"RESOURCE_KIND");
        }
        private static string OwningRoot() {
            string root=Path.GetFullPath(Environment.CurrentDirectory); NoReparse(root,true);
            using(var handle=Native.CreateFile(root,0x80,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) { Require(!handle.IsInvalid,"OWNING_ROOT_HANDLE"); Require(SamePath(FinalPath(handle),root),"OWNING_ROOT_CWD"); }
            Require(File.Exists(Path.Combine(root,"AGENTS.md"))&&File.Exists(Path.Combine(root,"Cargo.toml")),"OWNING_ROOT_MARKERS"); return root;
        }
        private static string OwnExecutable() { var path=new StringBuilder(32768); uint count=Native.GetModuleFileName(IntPtr.Zero,path,(uint)path.Capacity); Require(count>0&&count<path.Capacity,"OWN_IMAGE_BOUND"); return Path.GetFullPath(path.ToString()); }
        private static Buffer TokenData(Handle token,int type,int bytes,out uint length) {
            var output=new Buffer(bytes); try { Check(Native.GetTokenInformation(token,type,output.Pointer,(uint)bytes,out length),"TOKEN_QUERY_"+type); Require(length>0&&length<=bytes,"TOKEN_QUERY_EXTENT"); return output; } catch { output.Dispose(); throw; }
        }
        private static uint Dword(Handle token,int type) { uint length; using(var data=TokenData(token,type,4,out length)) { Require(length==4,"TOKEN_DWORD_EXTENT"); return unchecked((uint)Marshal.ReadInt32(data.Pointer)); } }
        private static byte[] Sid(Buffer buffer,uint returned,IntPtr pointer,int lower) {
            long offset=pointer.ToInt64()-buffer.Pointer.ToInt64(); Require(returned>=8&&offset>=lower&&offset<=(long)returned-8,"SID_HEADER_EXTENT");
            int count=Marshal.ReadByte(pointer,1); Require(Marshal.ReadByte(pointer)==1&&count<=15,"SID_HEADER"); int bytes=8+4*count;
            Require(offset<=(long)returned-bytes,"SID_BODY_EXTENT"); byte[] value=new byte[bytes]; Marshal.Copy(pointer,value,0,bytes); return value;
        }
        private static string SidText(byte[] sid) { ulong authority=0; for(int i=2;i<8;i++) authority=(authority<<8)|sid[i]; string result="S-1-"+authority;
            for(int i=0;i<sid[1];i++) result+="-"+BitConverter.ToUInt32(sid,8+4*i); return result; }
        private static bool ThreadAbsent(IntPtr thread) { Handle token; if(Native.OpenThreadToken(thread,Query,true,out token)) { token.Dispose(); return false; }
            int error=Marshal.GetLastWin32Error(); Require(error==1008,"THREAD_TOKEN_QUERY"); return true; }
        private static string KnownFolder(Handle token) { var id=new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"); IntPtr value=IntPtr.Zero;
            try { int error=Native.SHGetKnownFolderPath(ref id,0,token,out value); Require(error==0&&value!=IntPtr.Zero,"KNOWN_FOLDER_QUERY");
                // SHGetKnownFolderPath owns the complete null-terminated allocation.
                string path=Marshal.PtrToStringUni(value); Require(path!=null&&path.Length>0&&path.Length<32768,"KNOWN_FOLDER_BOUND"); NoReparse(path,true); return Path.GetFullPath(path); }
            finally { if(value!=IntPtr.Zero) Marshal.FreeCoTaskMem(value); } }
        private static string Creation(IntPtr process) { FileTime created,exited,kernel,user; Check(Native.GetProcessTimes(process,out created,out exited,out kernel,out user),"PROCESS_TIME"); Require(created.Value>0&&exited.Value==0,"PROCESS_LIVE_IDENTITY"); return created.Value.ToString(); }
        private static Context Observe(Handle token,IntPtr process,IntPtr thread,uint pid,string kind) {
            var value=new Context { ObservationKind=kind,Pid=pid,CreationFiletime=Creation(process),TokenType=Dword(token,8),Elevated=Dword(token,20)!=0,
                ElevationType=Dword(token,18),SessionId=Dword(token,12),ThreadTokenAbsent=ThreadAbsent(thread),LocalAppData=KnownFolder(token) };
            ushort machine,native; Check(Native.IsWow64Process2(process,out machine,out native),"ARCHITECTURE_QUERY"); value.ProcessMachine=machine; value.NativeMachine=native;
            uint length; using(var data=TokenData(token,1,4096,out length)) { Require(length>=Marshal.SizeOf<SidAttributes>(),"USER_HEADER"); value.UserSid=SidText(Sid(data,length,Marshal.ReadIntPtr(data.Pointer),Marshal.SizeOf<SidAttributes>())); }
            using(var data=TokenData(token,25,4096,out length)) { Require(length>=Marshal.SizeOf<SidAttributes>(),"INTEGRITY_HEADER"); var label=Marshal.PtrToStructure<SidAttributes>(data.Pointer); byte[] sid=Sid(data,length,label.Sid,Marshal.SizeOf<SidAttributes>());
                Require(sid.Length==12&&sid.Take(8).SequenceEqual(new byte[]{1,1,0,0,0,0,0,16}),"INTEGRITY_SID"); value.IntegrityRid=BitConverter.ToUInt32(sid,8); value.IntegrityAttributes=label.Attributes; }
            using(var data=TokenData(token,10,Marshal.SizeOf<TokenStatistics>(),out length)) { Require(length==Marshal.SizeOf<TokenStatistics>(),"STATISTICS_EXTENT"); var stats=Marshal.PtrToStructure<TokenStatistics>(data.Pointer);
                value.AuthenticationId=unchecked((uint)stats.AuthenticationId.High).ToString("x8")+stats.AuthenticationId.Low.ToString("x8"); }
            using(var data=TokenData(token,2,16384,out length)) { uint count=unchecked((uint)Marshal.ReadInt32(data.Pointer)); int offset=IntPtr.Size; int stride=Marshal.SizeOf<SidAttributes>(); Require(count<=256&&offset+(long)count*stride<=length,"GROUP_EXTENT");
                for(int i=0;i<count;i++) { var group=Marshal.PtrToStructure<SidAttributes>(IntPtr.Add(data.Pointer,offset+i*stride)); string sid=SidText(Sid(data,length,group.Sid,checked(offset+(int)count*stride)));
                    if(sid=="S-1-5-32-544") { Require(!value.AdministratorsPresent,"DUPLICATE_ADMIN_GROUP"); value.AdministratorsPresent=true; value.AdministratorsAttributes=group.Attributes; } } }
            Luid traversal; Check(Native.LookupPrivilegeValue(null,"SeChangeNotifyPrivilege",out traversal),"TRAVERSAL_PRIVILEGE");
            using(var data=TokenData(token,3,4096,out length)) { uint count=unchecked((uint)Marshal.ReadInt32(data.Pointer)); Require(count<=128&&4+(long)count*12==length,"PRIVILEGE_EXTENT");
                for(int i=0;i<count;i++) { IntPtr row=IntPtr.Add(data.Pointer,4+i*12); var luid=Marshal.PtrToStructure<Luid>(row); uint flags=unchecked((uint)Marshal.ReadInt32(row,8));
                    if((flags&2)!=0&&(luid.Low!=traversal.Low||luid.High!=traversal.High)) value.NonTraversalPrivilegeEnabled=true; } }
            return value;
        }
        private static Handle OwnToken(bool restriction=true) { Handle token; uint rights=restriction?Query|Duplicate|Assign|AdjustDefault|Impersonate:Query|Impersonate;
            Check(Native.OpenProcessToken(Native.GetCurrentProcess(),rights,out token),"OWN_PRIMARY_TOKEN"); Require(!token.IsInvalid,"OWN_TOKEN_HANDLE"); return token; }
        private static Handle Candidate(Handle source) {
            using(var admin=new Buffer(68)) using(var medium=new Buffer(68)) {
                uint adminBytes=68,mediumBytes=68; Check(Native.CreateWellKnownSid(26,IntPtr.Zero,admin.Pointer,ref adminBytes),"ADMIN_SID"); Check(Native.CreateWellKnownSid(67,IntPtr.Zero,medium.Pointer,ref mediumBytes),"MEDIUM_SID");
                Require(adminBytes<=68&&mediumBytes==12,"FIXED_SID_EXTENT"); var disabled=new SidAttributes { Sid=admin.Pointer,Attributes=0 }; Handle result;
                Check(Native.CreateRestrictedToken(source,5,1,ref disabled,0,IntPtr.Zero,0,IntPtr.Zero,out result),"RESTRICT_OWN_TOKEN");
                try { var label=new SidAttributes { Sid=medium.Pointer,Attributes=0x20 }; Check(Native.SetTokenInformation(result,25,ref label,(uint)(Marshal.SizeOf<SidAttributes>()+mediumBytes)),"LOWER_NEW_TOKEN_MEDIUM"); return result; }
                catch { result.Dispose(); throw; }
            }
        }
        private static bool IsOrdinary(Context c) { return c!=null&&c.TokenType==1&&!c.Elevated&&(c.ElevationType==1||c.ElevationType==3)&&c.IntegrityRid==8192&&(c.IntegrityAttributes&0x20)!=0&&c.ThreadTokenAbsent&&c.ProcessMachine==0&&c.NativeMachine==0x8664; }
        private static void Ordinary(Context c) { Require(IsOrdinary(c),"ORDINARY_CONTEXT_REQUIRED"); }
        private static void Complete(LaunchObservation value) {
            Require(value.AssignedBeforeResume&&value.ChildContextMatched&&value.ChildDesktopContextMatched&&value.ExitObserved&&value.ChildExitCode==0&&value.StdoutEof&&value.StderrEof&&value.IoThreadsJoined&&value.OwnedJobEmpty
                &&value.SourceToolFenceStable&&value.CleanupSettled&&!value.ReaderFailed&&!value.OutputOverflow&&!value.ForcedCleanup&&value.FailureCode==null,"SUCCESS_CUSTODY_REQUIRED");
        }
        private static void SameIdentity(Context source,Context candidate) { Require(source.UserSid==candidate.UserSid&&source.SessionId==candidate.SessionId&&source.AuthenticationId==candidate.AuthenticationId&&SamePath(source.LocalAppData,candidate.LocalAppData),"SAME_USER_PROFILE_REQUIRED"); }
        private static Handle Prepare(ProbeObservation probe) {
            using(var source=OwnToken()) {
                probe.SourceContext=Observe(source,Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcessId(),"own_process_token");
                Require(probe.SourceContext.TokenType==1&&probe.SourceContext.ThreadTokenAbsent&&probe.SourceContext.IntegrityRid>=8192&&probe.SourceContext.ProcessMachine==0&&probe.SourceContext.NativeMachine==0x8664,"SOURCE_CONTEXT_REQUIRED");
                probe.DerivedAttempted=true; Handle candidate=Candidate(source); try { probe.DerivedContext=Observe(candidate,Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcessId(),"derived_token_only"); Ordinary(probe.DerivedContext); SameIdentity(probe.SourceContext,probe.DerivedContext);
                    Require(!probe.DerivedContext.NonTraversalPrivilegeEnabled,"DERIVED_PRIVILEGES");
                    Require(!probe.SourceContext.AdministratorsPresent||(probe.DerivedContext.AdministratorsPresent&&(probe.DerivedContext.AdministratorsAttributes&0x10)!=0&&(probe.DerivedContext.AdministratorsAttributes&4)==0),"DERIVED_ADMIN_DENY_ONLY");
                    probe.DerivedPredicateAccepted=true; return candidate;
                } catch { candidate.Dispose(); throw; }
            }
        }
        // Microsoft TOKEN_DEFAULT_DACL contains a borrowed PACL in the query
        // output. Enforce containment before any dereference; IsValidAcl has no
        // extended error and must never receive NULL. AclSize includes free space.
        private static void SecurityRange(ulong basis,uint returned,ulong pointer,uint lower,uint bytes) {
            Require(bytes>=8&&returned<=SecurityBytes+IntPtr.Size&&returned>=bytes&&pointer>=basis,"SECURITY_POINTER_EXTENT");
            ulong offset=pointer-basis; Require(offset>=lower&&offset<=returned-bytes,"SECURITY_POINTER_EXTENT");
            Require(basis<=UInt64.MaxValue-returned,"SECURITY_POINTER_OVERFLOW");
        }
        private static ushort U16(byte[] data,int offset) { Require(offset>=0&&offset<=data.Length-2,"SECURITY_HEADER_EXTENT"); return BitConverter.ToUInt16(data,offset); }
        private static uint U32(byte[] data,int offset) { Require(offset>=0&&offset<=data.Length-4,"SECURITY_HEADER_EXTENT"); return BitConverter.ToUInt32(data,offset); }
        private static string AclBytes(byte[] data) {
            Require(data!=null&&data.Length>=8&&data.Length<=SecurityBytes&&(data[0]==2||data[0]==4)&&U16(data,2)==data.Length,"SECURITY_ACL_HEADER");
            int count=U16(data,4),offset=8; Require(count<=128,"SECURITY_ACL_COUNT");
            for(int i=0;i<count;i++) { Require(offset<=data.Length-4,"SECURITY_ACE_HEADER"); int bytes=U16(data,offset+2);
                Require(bytes>=4&&bytes%4==0&&bytes<=data.Length-offset,"SECURITY_ACE_EXTENT"); offset+=bytes; }
            return count==0?"empty":"populated";
        }
        private static void RelativeSid(byte[] data,uint offset) {
            if(offset==0) return; Require(offset>=20&&offset%4==0&&offset<=(uint)data.Length-8,"SECURITY_SID_OFFSET");
            int index=(int)offset; Require(data[index]==1&&data[index+1]<=15&&8+4*data[index+1]<=data.Length-index,"SECURITY_SID_EXTENT");
        }
        private static string DescriptorBytes(byte[] data) {
            Require(data!=null&&data.Length>=20&&data.Length<=SecurityBytes&&data[0]==1&&(U16(data,2)&0x8000)!=0,"SECURITY_DESCRIPTOR_HEADER");
            RelativeSid(data,U32(data,4)); RelativeSid(data,U32(data,8)); Require(U32(data,12)==0&&(U16(data,2)&0x10)==0,"SECURITY_SACL_NOT_REQUESTED");
            uint offset=U32(data,16); bool present=(U16(data,2)&4)!=0;
            if(!present) { Require(offset==0,"SECURITY_ABSENT_DACL_OFFSET"); return "absent"; }
            if(offset==0) return "null";
            Require(offset>=20&&offset%4==0&&offset<=(uint)data.Length-8,"SECURITY_DACL_OFFSET"); int bytes=U16(data,(int)offset+2);
            Require(bytes>=8&&bytes<=data.Length-offset,"SECURITY_DACL_EXTENT"); byte[] acl=new byte[bytes]; Array.Copy(data,(int)offset,acl,0,bytes); return AclBytes(acl);
        }
        private static void SecurityUnavailable(StartupSecurityPayload value,string code,uint error=0) {
            value.Result="unavailable"; value.AclState="unavailable"; value.Bytes=0; value.DataBase64=null; value.Sha256=null; value.FailureCode=code; value.NativeError=error;
        }
        private static void SecurityObserved(StartupSecurityPayload value,byte[] data,string state) {
            Require(data==null||data.Length<=SecurityBytes,"SECURITY_SNAPSHOT_BOUND"); value.Result="observed"; value.AclState=state; value.Bytes=(uint)(data==null?0:data.Length);
            value.DataBase64=data==null?null:Convert.ToBase64String(data); value.Sha256=data==null?null:Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(); value.FailureCode=null; value.NativeError=0;
        }
        private static byte[] SecurityPayload(StartupSecurityPayload value,bool token) {
            Require(value!=null,"SECURITY_PAYLOAD_REQUIRED");
            if(value.Result=="unavailable") { Require(value.AclState=="unavailable"&&value.Bytes==0&&value.DataBase64==null&&value.Sha256==null&&Regex.IsMatch(value.FailureCode??"",@"^[A-Z0-9_]{1,64}$"),"SECURITY_UNAVAILABLE_STATE"); return null; }
            Require(value.Result=="observed"&&value.FailureCode==null&&value.NativeError==0,"SECURITY_OBSERVED_STATE");
            if(token&&value.AclState=="null") { Require(value.Bytes==0&&value.DataBase64==null&&value.Sha256==null,"SECURITY_NULL_DEFAULT_DACL"); return null; }
            Require(value.Bytes>0&&value.Bytes<=SecurityBytes&&value.DataBase64!=null&&value.DataBase64.Length<=4*((SecurityBytes+2)/3)&&Regex.IsMatch(value.Sha256??"",@"^[0-9a-f]{64}$"),"SECURITY_PAYLOAD_BOUND");
            byte[] buffer=new byte[SecurityBytes]; int count; Require(Convert.TryFromBase64String(value.DataBase64,buffer,out count)&&count==value.Bytes,"SECURITY_PAYLOAD_ENCODING");
            byte[] raw=new byte[count]; Array.Copy(buffer,raw,count); Require(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant()==value.Sha256,"SECURITY_PAYLOAD_HASH");
            Require((token?AclBytes(raw):DescriptorBytes(raw))==value.AclState,"SECURITY_ACL_STATE"); return raw;
        }
        private static void SecurityIdentity(OwnedChildSecurityObservation value,string role,uint pid,string created,uint tid) {
            Require(value!=null&&value.ObservationKind=="owned_child_kernel_object_security"&&value.Role==role&&value.Phase=="assigned_before_resume"&&value.ObjectType==6&&value.RequestedInformation==7,"SECURITY_OBJECT_ROLE_PHASE");
            Require(pid>0&&tid>0&&Regex.IsMatch(created??"",@"^[1-9][0-9]{0,19}$")&&value.Pid==pid&&value.CreationFiletime==created&&value.InitialThreadId==tid,"SECURITY_OBJECT_IDENTITY"); SecurityPayload(value,false);
        }
        private static void SecurityClosed(JsonElement value,string[] metadata) { Closed(value,metadata.Concat(new[]{"result","aclState","bytes","dataBase64","sha256","failureCode","nativeError"}).ToArray()); }
        private static StartupSecurityObservation SecurityFrom(JsonElement input,string selectedKind,uint pid,string created,uint tid,bool assigned) {
            Closed(input,new[]{"schemaVersion","selectedTokenDefaultDacl","ownedChildObjects"});
            SecurityClosed(input.GetProperty("selectedTokenDefaultDacl"),new[]{"observationKind","selectedTokenObservationKind","tokenInformationClass","returnedBytes"});
            var objects=input.GetProperty("ownedChildObjects"); Require(objects.ValueKind==JsonValueKind.Null||objects.ValueKind==JsonValueKind.Array,"SECURITY_OBJECT_ARRAY");
            Require(assigned==(objects.ValueKind==JsonValueKind.Array),"SECURITY_OBJECT_PHASE_PRESENCE");
            if(objects.ValueKind==JsonValueKind.Array) { Require(objects.GetArrayLength()==2,"SECURITY_OBJECT_CARDINALITY"); foreach(var item in objects.EnumerateArray()) SecurityClosed(item,new[]{"observationKind","role","phase","pid","creationFiletime","initialThreadId","objectType","requestedInformation"}); }
            var value=JsonSerializer.Deserialize<StartupSecurityObservation>(input.GetRawText(),JsonOptions);
            Require(value.SchemaVersion=="bridge-windows-journal-ci-startup-security/v1"&&value.SelectedTokenDefaultDacl.ObservationKind=="selected_primary_token_default_dacl"&&value.SelectedTokenDefaultDacl.SelectedTokenObservationKind==selectedKind&&(selectedKind=="own_process_token"||selectedKind=="derived_token_only")&&value.SelectedTokenDefaultDacl.TokenInformationClass==6,"SECURITY_TOKEN_BINDING");
            var token=value.SelectedTokenDefaultDacl; SecurityPayload(token,true);
            if(token.Result=="observed") Require(token.ReturnedBytes>=IntPtr.Size+token.Bytes&&token.ReturnedBytes<=SecurityBytes+IntPtr.Size&&(token.AclState!="null"||token.ReturnedBytes==IntPtr.Size),"SECURITY_TOKEN_RETURNED_EXTENT");
            if(value.OwnedChildObjects!=null) { SecurityIdentity(value.OwnedChildObjects[0],"process",pid,created,tid); SecurityIdentity(value.OwnedChildObjects[1],"thread",pid,created,tid); }
            return value;
        }
        private static TokenDefaultDaclObservation DefaultDacl(Handle primary,Context selected) {
            var result=new TokenDefaultDaclObservation { SelectedTokenObservationKind=selected.ObservationKind }; Handle owned=null;
            try {
                if(primary==null) { Check(Native.OpenProcessToken(Native.GetCurrentProcess(),Query,out owned),"DEFAULT_DACL_OWN_TOKEN"); Require(!owned.IsInvalid,"DEFAULT_DACL_TOKEN_HANDLE"); primary=owned;
                    Ordinary(selected); Require(Dword(primary,8)==1&&Dword(primary,20)==0&&Dword(primary,18)==selected.ElevationType&&Dword(primary,12)==selected.SessionId&&ThreadAbsent(Native.GetCurrentThread()),"DEFAULT_DACL_OWN_CONTEXT");
                    uint extent; using(var user=TokenData(primary,1,4096,out extent)) { Require(extent>=Marshal.SizeOf<SidAttributes>(),"DEFAULT_DACL_USER_HEADER"); Require(SidText(Sid(user,extent,Marshal.ReadIntPtr(user.Pointer),Marshal.SizeOf<SidAttributes>()))==selected.UserSid,"DEFAULT_DACL_OWN_USER"); }
                    using(var integrity=TokenData(primary,25,4096,out extent)) { Require(extent>=Marshal.SizeOf<SidAttributes>(),"DEFAULT_DACL_INTEGRITY_HEADER"); var label=Marshal.PtrToStructure<SidAttributes>(integrity.Pointer); byte[] sid=Sid(integrity,extent,label.Sid,Marshal.SizeOf<SidAttributes>());
                        Require(sid.Length==12&&sid.Take(8).SequenceEqual(new byte[]{1,1,0,0,0,0,0,16})&&BitConverter.ToUInt32(sid,8)==selected.IntegrityRid&&label.Attributes==selected.IntegrityAttributes,"DEFAULT_DACL_OWN_INTEGRITY"); }
                    using(var stats=TokenData(primary,10,Marshal.SizeOf<TokenStatistics>(),out extent)) { Require(extent==Marshal.SizeOf<TokenStatistics>(),"DEFAULT_DACL_STATISTICS_EXTENT"); var row=Marshal.PtrToStructure<TokenStatistics>(stats.Pointer); Require(unchecked((uint)row.AuthenticationId.High).ToString("x8")+row.AuthenticationId.Low.ToString("x8")==selected.AuthenticationId,"DEFAULT_DACL_OWN_LOGON"); }
                }
                using(var data=new Buffer(SecurityBytes+IntPtr.Size)) { uint returned; bool queried=Native.GetTokenInformation(primary,6,data.Pointer,(uint)data.Length,out returned); int error=Marshal.GetLastWin32Error(); result.ReturnedBytes=returned;
                    if(!queried) throw new CiFailure("DEFAULT_DACL_QUERY",error); Require(returned>=IntPtr.Size&&returned<=data.Length,"DEFAULT_DACL_RETURNED_EXTENT"); IntPtr acl=Marshal.ReadIntPtr(data.Pointer);
                    if(acl==IntPtr.Zero) { Require(returned==IntPtr.Size,"DEFAULT_DACL_NULL_EXTENT"); SecurityObserved(result,null,"null"); }
                    else { ulong basis=unchecked((ulong)data.Pointer.ToInt64()),pointer=unchecked((ulong)acl.ToInt64()); SecurityRange(basis,returned,pointer,(uint)IntPtr.Size,8); uint bytes=unchecked((ushort)Marshal.ReadInt16(acl,2)); Require(bytes>=8&&bytes<=SecurityBytes,"DEFAULT_DACL_ACL_BOUND"); SecurityRange(basis,returned,pointer,(uint)IntPtr.Size,bytes);
                        byte[] raw=new byte[bytes]; Marshal.Copy(acl,raw,0,raw.Length); string state=AclBytes(raw); Require(Native.IsValidAcl(acl),"DEFAULT_DACL_INVALID_ACL"); SecurityObserved(result,raw,state); }
                }
            } catch(CiFailure error) { SecurityUnavailable(result,error.Code,unchecked((uint)error.NativeError)); } catch { SecurityUnavailable(result,"DEFAULT_DACL_EXCEPTION"); }
            finally { if(owned!=null) owned.Dispose(); } return result;
        }
        private static OwnedChildSecurityObservation OwnedSecurity(Handle handle,string role,uint pid,string created,uint tid) {
            var result=new OwnedChildSecurityObservation { Role=role,Pid=pid,CreationFiletime=created,InitialThreadId=tid }; IntPtr descriptor=IntPtr.Zero;
            try { IntPtr owner,group,dacl,sacl; uint error=Native.GetSecurityInfo(handle.DangerousGetHandle(),6,7,out owner,out group,out dacl,out sacl,out descriptor);
                // GetSecurityInfo returns its DWORD error directly. Its descriptor
                // is self-relative and owns all component pointers; LocalFree once.
                if(error!=0) { SecurityUnavailable(result,"OWNED_SECURITY_QUERY",error); return result; }
                Require(descriptor!=IntPtr.Zero&&Native.IsValidSecurityDescriptor(descriptor),"OWNED_SECURITY_DESCRIPTOR"); ushort control; uint revision; Check(Native.GetSecurityDescriptorControl(descriptor,out control,out revision),"OWNED_SECURITY_CONTROL"); Require((control&0x8000)!=0&&revision==1,"OWNED_SECURITY_SELF_RELATIVE");
                uint bytes=Native.GetSecurityDescriptorLength(descriptor); Require(bytes>=20&&bytes<=SecurityBytes,"OWNED_SECURITY_BOUND"); byte[] raw=new byte[bytes]; Marshal.Copy(descriptor,raw,0,raw.Length); SecurityObserved(result,raw,DescriptorBytes(raw));
            } catch(CiFailure error) { SecurityUnavailable(result,error.Code,unchecked((uint)error.NativeError)); } catch { SecurityUnavailable(result,"OWNED_SECURITY_EXCEPTION"); }
            finally { try { if(descriptor!=IntPtr.Zero&&Native.LocalFree(descriptor)!=IntPtr.Zero&&result.Result=="observed") SecurityUnavailable(result,"OWNED_SECURITY_LOCAL_FREE"); }
                catch { if(result.Result=="observed") SecurityUnavailable(result,"OWNED_SECURITY_FREE_EXCEPTION"); } } return result;
        }
        private static int SerializedBytes(object value) { return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value,JsonOptions)+"\n"); }
        private static void SecurityBudget(int diagnosticBytes,int receiptBytes) { Require(diagnosticBytes>=0&&receiptBytes>=0&&diagnosticBytes<=SecurityDiagnosticBytes&&receiptBytes<=ReceiptBytes,"STARTUP_SECURITY_RECEIPT_BOUND"); }
        private static void ValidateStartupSecurity(LaunchObservation result,uint tid) {
            SecurityBudget(SerializedBytes(result.StartupSecurity),SerializedBytes(result));
            string selectedKind=result.LaunchRoute=="ordinary-own-process"?"own_process_token":result.LaunchRoute=="restricted-primary"?"derived_token_only":null;
            using(var parsed=JsonDocument.Parse(JsonSerializer.Serialize(result.StartupSecurity,JsonOptions))) SecurityFrom(parsed.RootElement,selectedKind,result.ChildPid,result.ChildCreationFiletime,tid,result.AssignedBeforeResume);
        }
        private static void FitStartupSecurity(LaunchObservation result,uint tid) {
            if(result.StartupSecurity==null) return;
            try { ValidateStartupSecurity(result,tid); }
            catch { try { SecurityUnavailable(result.StartupSecurity.SelectedTokenDefaultDacl,"STARTUP_SECURITY_RECEIPT_BOUND"); if(result.StartupSecurity.OwnedChildObjects!=null) foreach(var item in result.StartupSecurity.OwnedChildObjects) SecurityUnavailable(item,"STARTUP_SECURITY_RECEIPT_BOUND");
                // Optional observations cannot make an otherwise bounded receipt
                // fail. If even their fixed metadata cannot fit, omit the bundle.
                ValidateStartupSecurity(result,tid); }
                catch { result.StartupSecurity=null; } }
        }
        // Read-only snapshots of the parent's current objects. These neither select
        // the child's desktop nor establish MIC, loader or native-test success.
        private static SecuritySnapshot Snapshot(IntPtr handle,uint information) {
            var result=new SecuritySnapshot { RequestedInformation=information };
            try {
                uint needed; bool sized=Native.GetUserObjectSecurity(handle,ref information,IntPtr.Zero,0,out needed); int sizeError=Marshal.GetLastWin32Error(); result.RequiredBytes=needed;
                if(sized||sizeError!=122) throw new CiFailure("USER_SECURITY_SIZE_QUERY",sizeError); Require(needed>=20&&needed<=4096,"USER_SECURITY_BOUND");
                using(var buffer=new Buffer((int)needed)) {
                    uint returned; bool read=Native.GetUserObjectSecurity(handle,ref information,buffer.Pointer,needed,out returned); int readError=Marshal.GetLastWin32Error(); result.RequiredBytes=returned;
                    if(!read) throw new CiFailure("USER_SECURITY_QUERY",readError);
                    Require(returned>=20&&returned<=needed&&Native.IsValidSecurityDescriptor(buffer.Pointer),"USER_SECURITY_EXTENT");
                    ushort control; uint revision; Check(Native.GetSecurityDescriptorControl(buffer.Pointer,out control,out revision),"USER_SECURITY_CONTROL"); Require((control&0x8000)!=0&&revision==1,"USER_SECURITY_SELF_RELATIVE");
                    uint length=Native.GetSecurityDescriptorLength(buffer.Pointer); Require(length>=20&&length<=returned,"USER_SECURITY_LENGTH");
                    result.Raw=new byte[(int)length]; Marshal.Copy(buffer.Pointer,result.Raw,0,result.Raw.Length);
                    result.DescriptorBase64=Convert.ToBase64String(result.Raw); result.Sha256=Convert.ToHexString(SHA256.HashData(result.Raw)).ToLowerInvariant(); result.Result="observed";
                }
            } catch(CiFailure error) { result.FailureCode=error.Code; result.NativeError=error.NativeError; }
            catch { result.FailureCode="USER_SECURITY_EXCEPTION"; }
            return result;
        }
        private static void DesktopComponent(string value) {
            Require(value!=null&&value.Length>0&&value.Length<=512,"DESKTOP_NAME_BOUND");
            for(int i=0;i<value.Length;i++) { char c=value[i]; Require(!Char.IsControl(c)&&c!='\\'&&c!='/',"DESKTOP_NAME_COMPONENT");
                if(Char.IsHighSurrogate(c)) { Require(i+1<value.Length&&Char.IsLowSurrogate(value[i+1]),"DESKTOP_NAME_UTF16"); i++; }
                else Require(!Char.IsLowSurrogate(c),"DESKTOP_NAME_UTF16"); }
        }
        private static string DesktopRoute(DesktopContext value) {
            Require(value!=null&&value.ObservationKind=="own_process_window_station_and_current_thread_desktop","DESKTOP_OBSERVATION_KIND");
            DesktopComponent(value.WindowStationName); DesktopComponent(value.ThreadDesktopName); return value.WindowStationName+"\\"+value.ThreadDesktopName;
        }
        private static void DesktopRequest(string value) {
            Require(value!=null&&value.Length>=3&&value.Length<=1025,"DESKTOP_REQUEST_BOUND"); string[] parts=value.Split('\\'); Require(parts.Length==2,"DESKTOP_REQUEST_COMPONENTS"); DesktopComponent(parts[0]); DesktopComponent(parts[1]);
        }
        private static void DesktopMatches(DesktopContext own,string requested) { DesktopRequest(requested); Require(String.Equals(DesktopRoute(own),requested,StringComparison.OrdinalIgnoreCase),"CHILD_DESKTOP_MISMATCH"); }
        private static string UserObjectName(IntPtr handle) {
            Require(handle!=IntPtr.Zero&&handle!=new IntPtr(-1),"OWN_USER_OBJECT_HANDLE");
            using(var name=new Buffer(1026)) {
                uint needed; Check(Native.GetUserObjectInformation(handle,2,name.Pointer,1026,out needed),"USER_OBJECT_NAME");
                Require(needed>=2&&needed<=1026&&needed%2==0&&Marshal.ReadInt16(name.Pointer,(int)needed-2)==0,"USER_OBJECT_NAME_EXTENT");
                string value=Marshal.PtrToStringUni(name.Pointer,(int)needed/2-1); DesktopComponent(value); return value;
            }
        }
        private static DesktopContext OwnDesktop() {
            // Borrowed handles are queried, never inherited, switched or closed.
            var value=new DesktopContext { WindowStationName=UserObjectName(Native.GetProcessWindowStation()),ThreadDesktopName=UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId())) };
            DesktopRoute(value); Require(ThreadAbsent(Native.GetCurrentThread()),"DESKTOP_THREAD_TOKEN_ABSENT"); return value;
        }
        private static DesktopContext DesktopContextFrom(JsonElement input) {
            Closed(input,new[]{"observationKind","windowStationName","threadDesktopName"});
            var value=JsonSerializer.Deserialize<DesktopContext>(input.GetRawText(),JsonOptions); DesktopRoute(value); return value;
        }
        private static void StartupObject(IntPtr handle,Handle token,StartupObjectObservation result) {
            try {
                Require(handle!=IntPtr.Zero&&handle!=new IntPtr(-1),"PARENT_USER_OBJECT_HANDLE");
                result.Name=UserObjectName(handle);
                using(var flags=new Buffer(Marshal.SizeOf<UserObjectFlags>())) {
                    uint needed; Check(Native.GetUserObjectInformation(handle,1,flags.Pointer,(uint)flags.Length,out needed),"USER_OBJECT_FLAGS"); Require(needed==flags.Length,"USER_OBJECT_FLAGS_EXTENT");
                    result.Flags=Marshal.PtrToStructure<UserObjectFlags>(flags.Pointer).Flags;
                }
                result.OwnerGroupDacl=Snapshot(handle,7); result.MandatoryLabel=Snapshot(handle,0x10);
                GenericMapping mapping;
                if(result.Role=="window-station") {
                    bool interactive=String.Equals(result.Name,"WinSta0",StringComparison.OrdinalIgnoreCase); result.MappingKind=interactive?"interactive-window-station":"noninteractive-window-station";
                    mapping=interactive?new GenericMapping { Read=0x20303,Write=0x2001c,Execute=0x20060,All=0xf037f }:new GenericMapping { Read=0x20103,Write=0x2000c,Execute=0x20060,All=0xf016f };
                } else { Require(result.Role=="desktop","FIXED_USER_OBJECT_ROLE"); result.MappingKind="desktop"; mapping=new GenericMapping { Read=0x20041,Write=0x200be,Execute=0x20100,All=0xf01ff }; }
                Require(result.OwnerGroupDacl.Result=="observed","DACL_SNAPSHOT_UNAVAILABLE");
                using(var descriptor=new Buffer(result.OwnerGroupDacl.Raw.Length)) using(var privileges=new Buffer(4096)) {
                    Marshal.Copy(result.OwnerGroupDacl.Raw,0,descriptor.Pointer,descriptor.Length); uint bytes=4096,granted; bool status;
                    Check(Native.AccessCheck(descriptor.Pointer,token,result.DesiredAccess,ref mapping,privileges.Pointer,ref bytes,out granted,out status),"PARENT_OBJECT_DACL_CHECK");
                    result.DaclCheckApiSucceeded=true; result.DaclAccessStatus=status; result.DaclGrantedAccess=granted;
                    Require(bytes>=8&&bytes<=4096,"ACCESS_CHECK_PRIVILEGE_EXTENT"); uint count=unchecked((uint)Marshal.ReadInt32(privileges.Pointer)); Require(count<=128&&8+(long)count*12<=bytes,"ACCESS_CHECK_PRIVILEGE_COUNT");
                    byte[] used=new byte[8+(int)count*12]; Marshal.Copy(privileges.Pointer,used,0,used.Length); result.PrivilegeSetBase64=Convert.ToBase64String(used);
                }
            } catch(CiFailure error) { result.FailureCode=error.Code; result.NativeError=error.NativeError; }
            catch { result.FailureCode="PARENT_OBJECT_OBSERVATION_EXCEPTION"; }
        }
        private static StartupObjectObservation[] StartupObjects(Handle primary,Context selected) {
            var result=new[]{new StartupObjectObservation { Role="window-station",SelectedTokenObservationKind=selected.ObservationKind },new StartupObjectObservation { Role="desktop",SelectedTokenObservationKind=selected.ObservationKind }};
            Handle owned=null,duplicate=null;
            try {
                if(primary==null) {
                    Check(Native.OpenProcessToken(Native.GetCurrentProcess(),Query|Duplicate|Impersonate,out owned),"STARTUP_OWN_QUERY_DUPLICATE_TOKEN"); Require(!owned.IsInvalid,"STARTUP_OWN_TOKEN_HANDLE"); primary=owned;
                    var current=Observe(primary,Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcessId(),"own_process_token"); Ordinary(current); SameIdentity(selected,current);
                }
                Check(Native.DuplicateTokenEx(primary,Query,IntPtr.Zero,2,2,out duplicate),"STARTUP_ACCESS_CHECK_DUPLICATE"); Require(!duplicate.IsInvalid,"STARTUP_DUPLICATE_HANDLE"); uint type=Dword(duplicate,8); Require(type==2,"STARTUP_ACCESS_CHECK_TOKEN_TYPE");
                foreach(var item in result) item.AccessCheckTokenType=type;
                // GetProcessWindowStation/GetThreadDesktop return borrowed handles.
                // No impersonation, desktop switch, security write or handle close.
                StartupObject(Native.GetProcessWindowStation(),duplicate,result[0]); StartupObject(Native.GetThreadDesktop(Native.GetCurrentThreadId()),duplicate,result[1]);
                Require(ThreadAbsent(Native.GetCurrentThread()),"STARTUP_THREAD_TOKEN_ABSENT");
            } catch(CiFailure error) { foreach(var item in result) { item.FailureCode=error.Code; item.NativeError=error.NativeError; } }
            catch { foreach(var item in result) item.FailureCode="STARTUP_OBSERVATION_EXCEPTION"; }
            finally { if(duplicate!=null) duplicate.Dispose(); if(owned!=null) owned.Dispose(); }
            return result;
        }
        public static string Probe() { var probe=new ProbeObservation(); try {
                using(var source=OwnToken(false)) probe.SourceContext=Observe(source,Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcessId(),"own_process_token");
                if(IsOrdinary(probe.SourceContext)) probe.StartupObjects=StartupObjects(null,probe.SourceContext);
                else using(var candidate=Prepare(probe)) { probe.StartupObjects=StartupObjects(candidate,probe.DerivedContext); }
            } catch(CiFailure error) { probe.FailureCode=error.Code; probe.NativeError=error.NativeError; } catch { probe.FailureCode="PROBE_EXCEPTION"; }
            return JsonSerializer.Serialize(probe,JsonOptions); }
        private static void EnvironmentPreflight() {
            foreach(System.Collections.DictionaryEntry row in Environment.GetEnvironmentVariables()) { string key=((string)row.Key).ToUpperInvariant(),value=(string)row.Value;
                bool reserved=Regex.IsMatch(key,@"^(BRIDGE_(PRIVATE_JOURNAL_|FIXTURE_|TEST_WINDOWS_|WINDOWS_CHILD_|LOCAL_HOST_CHILD_|CONFIGURATION_|JOURNAL_CI_)|RUST_TEST_|LIBTEST_)")
                    ||Regex.IsMatch(key,@"^(RUSTC|RUSTDOC|RUSTFMT|CARGO|RUSTC_WRAPPER|RUSTC_WORKSPACE_WRAPPER|RUSTFLAGS|RUSTDOCFLAGS|RUSTUP_TOOLCHAIN|RUSTC_BOOTSTRAP|CARGO_TARGET_DIR|CARGO_INCREMENTAL|CARGO_ENCODED_RUSTFLAGS|CARGO_ENCODED_RUSTDOCFLAGS)$")
                    ||Regex.IsMatch(key,@"^CARGO_(TARGET_|PROFILE_|BUILD_|ALIAS_)")||Regex.IsMatch(key,@"^(NODE_OPTIONS|NODE_PATH|TS_NODE_.*|TSX_.*|VITE_.*|VITEST_.*|ESBUILD_BINARY_PATH|ROLLDOWN_BINDING_PATH|NAPI_RS_NATIVE_LIBRARY_PATH|NPM_CONFIG_(NODE_OPTIONS|SCRIPT_SHELL)|__COMPAT_LAYER|LD_PRELOAD|LD_LIBRARY_PATH|DYLD_.*)$");
                Require(!reserved||String.IsNullOrEmpty(value),"CALLER_EXECUTION_OVERRIDE");
            }
        }
        private static string NodePath() { string search=Environment.GetEnvironmentVariable("PATH"); Require(search!=null&&search.Length<=65536,"PATH_BOUND"); string[] directories=search.Split(';'); Require(directories.Length<=512,"PATH_BOUND");
            foreach(string directory in directories) { if(String.IsNullOrWhiteSpace(directory)) continue; Require(Path.IsPathFullyQualified(directory)&&!directory.Contains('"')&&!directory.Contains('\r')&&!directory.Contains('\n'),"PATH_ROUTE"); string candidate=Path.Combine(directory,"node.exe");
                if(File.Exists(candidate)) { NoReparse(candidate,false); string version=FileVersionInfo.GetVersionInfo(candidate).ProductVersion; Require(version=="24.14.1"||version=="24.14.1.0","NODE_PIN"); return Path.GetFullPath(candidate); } }
            throw new CiFailure("NODE_MISSING"); }
        private static List<FileFence> Fences(string root) { var result=new List<FileFence>(); try { foreach(string name in SourceNames) result.Add(new FileFence(name,Path.Combine(root,"scripts","next",name),2*1024*1024));
                result.Add(new FileFence("powershell",OwnExecutable(),256L*1024*1024)); result.Add(new FileFence("node",NodePath(),256L*1024*1024)); return result; } catch { foreach(var item in result) item.Dispose(); throw; } }
        private static void Verify(List<FileFence> files) { foreach(var file in files) file.Verify(); }
        private static string ArtifactDirectory(string root,string id,bool create) { Require(Guid.TryParseExact(id,"D",out var parsed)&&parsed.ToString("D")==id,"ARTIFACT_ID");
            string path=root; foreach(string part in new[]{"artifacts","next","windows-journal-ci",id}) { path=Path.Combine(path,part);
                if(create&&part==id) Check(Native.CreateDirectory(path,IntPtr.Zero),"FRESH_ARTIFACT_DIRECTORY");
                else if(create&&!Directory.Exists(path)) Directory.CreateDirectory(path); NoReparse(path,true); }
            return path; }
        private static void Save(string directory,string name,object value) { byte[] data=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,JsonOptions)+"\n"); Require(data.Length<=65536,"RECEIPT_BOUND"); NoReparse(directory,true);
            using(var file=new FileStream(Path.Combine(directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read)) { file.Write(data,0,data.Length); file.Flush(true); } }
        private static string Quote(string value) {
            Require(!value.Contains('\0')&&!value.Contains('\r')&&!value.Contains('\n'),"COMMAND_ARGUMENT");
            var result=new StringBuilder("\""); int slashes=0;
            foreach(char character in value) { if(character=='\\') { slashes++; continue; }
                result.Append('\\',character=='"'?2*slashes+1:slashes); result.Append(character); slashes=0; }
            result.Append('\\',2*slashes); result.Append('"'); return result.ToString();
        }
        private static CreatedProcess Spawn(Handle token,string application,string[] arguments,string root,IntPtr stdin,IntPtr stdout,IntPtr stderr,string requestedDesktop=null) {
            var owned=new List<Handle>(); IntPtr attributes=IntPtr.Zero,handles=IntPtr.Zero,desktop=IntPtr.Zero; bool initialized=false; var custody=new CreatedProcess(); bool createdSuccessfully=false;
            try { foreach(IntPtr input in new[]{stdin,stdout,stderr}) { Require(input!=IntPtr.Zero&&input!=new IntPtr(-1),"STANDARD_HANDLE"); Handle copy; Check(Native.DuplicateHandle(Native.GetCurrentProcess(),input,Native.GetCurrentProcess(),out copy,0,true,2),"STANDARD_HANDLE_COPY"); owned.Add(copy); }
                if(requestedDesktop!=null) { DesktopRequest(requestedDesktop); desktop=Marshal.StringToHGlobalUni(requestedDesktop); }
                UIntPtr size=UIntPtr.Zero; Native.InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size); Require(size.ToUInt64()>0&&size.ToUInt64()<=65536,"ATTRIBUTE_BOUND"); attributes=Marshal.AllocHGlobal((int)size.ToUInt64()); Check(Native.InitializeProcThreadAttributeList(attributes,1,0,ref size),"ATTRIBUTE_INITIALIZE"); initialized=true;
                handles=Marshal.AllocHGlobal(3*IntPtr.Size); for(int i=0;i<3;i++) Marshal.WriteIntPtr(handles,i*IntPtr.Size,owned[i].DangerousGetHandle());
                Check(Native.UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20002),handles,new UIntPtr((uint)(3*IntPtr.Size)),IntPtr.Zero,IntPtr.Zero),"HANDLE_LIST");
                var startup=new StartupInfoEx { Startup=new StartupInfo { Size=(uint)Marshal.SizeOf<StartupInfoEx>(),Desktop=desktop,Flags=0x100,Input=owned[0].DangerousGetHandle(),Output=owned[1].DangerousGetHandle(),Error=owned[2].DangerousGetHandle() },Attributes=attributes };
                var command=new StringBuilder(String.Join(" ",new[]{application}.Concat(arguments).Select(Quote))); Require(command.Length<=32767,"COMMAND_BOUND"); ProcessInformation process;
                bool created=token==null?Native.CreateProcess(application,command,IntPtr.Zero,IntPtr.Zero,true,0x08080004,IntPtr.Zero,root,ref startup,out process):Native.CreateProcessAsUser(token,application,command,IntPtr.Zero,IntPtr.Zero,true,0x08080004,IntPtr.Zero,root,ref startup,out process);
                Check(created,"FIXED_PROCESS_CREATE"); custody.Process.Attach(process.Process); custody.Thread.Attach(process.Thread); custody.Pid=process.Pid; custody.Tid=process.Tid; createdSuccessfully=true;
                Require(!custody.Process.IsInvalid&&!custody.Thread.IsInvalid&&process.Pid!=0,"PROCESS_INFORMATION"); return custody;
            } catch { if(createdSuccessfully&&!custody.Process.IsInvalid) { Native.TerminateProcess(custody.Process,1); Native.WaitForSingleObject(custody.Process,1000); } custody.Process.Dispose(); custody.Thread.Dispose(); throw;
            } finally { foreach(var handle in owned) handle.Dispose(); if(initialized) Native.DeleteProcThreadAttributeList(attributes); if(attributes!=IntPtr.Zero) Marshal.FreeHGlobal(attributes); if(handles!=IntPtr.Zero) Marshal.FreeHGlobal(handles); if(desktop!=IntPtr.Zero) Marshal.FreeHGlobal(desktop); }
        }
        private static bool JobEmpty(Handle job) { Accounting value; Check(Native.QueryInformationJobObject(job,1,out value,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero),"JOB_ACCOUNTING"); return value.Active==0; }
        private static void Closed(JsonElement value,string[] keys) { Require(value.ValueKind==JsonValueKind.Object,"HANDSHAKE_OBJECT"); var names=new HashSet<string>(StringComparer.Ordinal); foreach(var item in value.EnumerateObject()) Require(names.Add(item.Name)&&keys.Contains(item.Name),"HANDSHAKE_KEYS"); Require(names.Count==keys.Length,"HANDSHAKE_KEYS"); }
        private static byte[] ReadHandshake() { using(var input=Console.OpenStandardInput()) using(var output=new MemoryStream()) { byte[] block=new byte[4096]; int count; while((count=input.Read(block,0,block.Length))!=0) { Require(output.Length+count<=65536,"HANDSHAKE_BOUND"); output.Write(block,0,count); } return output.ToArray(); } }
        private static Context ContextFrom(JsonElement input) { string[] keys={"observationKind","pid","creationFiletime","tokenType","elevated","elevationType","integrityRid","integrityAttributes","threadTokenAbsent","processMachine","nativeMachine","userSid","sessionId","authenticationId","localAppData","administratorsPresent","administratorsAttributes","nonTraversalPrivilegeEnabled"}; Closed(input,keys);
            var value=JsonSerializer.Deserialize<Context>(input.GetRawText(),JsonOptions); Require(value.Pid>0&&Regex.IsMatch(value.CreationFiletime??"",@"^[1-9][0-9]{0,19}$")&&Regex.IsMatch(value.AuthenticationId??"",@"^[0-9a-f]{16}$")&&value.UserSid!=null&&value.LocalAppData!=null,"HANDSHAKE_CONTEXT"); return value; }
        // Called only by the fixed child script. It self-observes before starting Node.
        public static int Child() {
            string directory=null; List<FileFence> files=null; Handle process=null,thread=null;
            try { EnvironmentPreflight(); string root=OwningRoot(); files=Fences(root); byte[] raw=ReadHandshake(); Require(raw.Length>0,"HANDSHAKE_EMPTY");
                using(var document=JsonDocument.Parse(raw,new JsonDocumentOptions { MaxDepth=24,CommentHandling=JsonCommentHandling.Disallow,AllowTrailingCommas=false })) {
                    var input=document.RootElement; Closed(input,new[]{"schemaVersion","artifactId","sourceContext","selectedContext","launchRoute","requestedDesktop","files"}); Require(input.GetProperty("schemaVersion").GetString()=="bridge-windows-journal-ci-handshake/v2","HANDSHAKE_SCHEMA");
                    directory=ArtifactDirectory(root,input.GetProperty("artifactId").GetString(),false); var expected=ContextFrom(input.GetProperty("sourceContext")); var selected=ContextFrom(input.GetProperty("selectedContext")); Ordinary(selected); SameIdentity(expected,selected);
                    string route=input.GetProperty("launchRoute").GetString(); Require(route=="ordinary-own-process"||route=="restricted-primary","HANDSHAKE_ROUTE");
                    if(route=="ordinary-own-process") { Ordinary(expected); Require(selected.ObservationKind=="own_process_token","HANDSHAKE_ROUTE_CONTEXT"); } else Require(selected.ObservationKind=="derived_token_only","HANDSHAKE_ROUTE_CONTEXT");
                    using(var token=OwnToken(false)) { var own=Observe(token,Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcessId(),"child_bootstrap_own_process_token"); Ordinary(own); SameIdentity(expected,own); SameIdentity(selected,own);
                        var supplied=input.GetProperty("files"); Require(supplied.ValueKind==JsonValueKind.Array&&supplied.GetArrayLength()==files.Count,"HANDSHAKE_FILES"); int index=0;
                        foreach(var item in supplied.EnumerateArray()) { Closed(item,new[]{"role","route","bytes","sha256","fileIdentity"}); var observed=files[index++].Observation; Require(item.GetProperty("role").GetString()==observed.Role&&SamePath(item.GetProperty("route").GetString(),observed.Route)&&item.GetProperty("bytes").GetInt64()==observed.Bytes&&item.GetProperty("sha256").GetString()==observed.Sha256&&item.GetProperty("fileIdentity").GetString()==observed.FileIdentity,"CHILD_SOURCE_TOOL_BINDING"); }
                        string requested=input.GetProperty("requestedDesktop").GetString(); DesktopRequest(requested); var desktop=OwnDesktop();
                        Save(directory,"child-context.json",new { SchemaVersion="bridge-windows-journal-ci-child-context/v2",Context=own,RequestedDesktop=requested,DesktopContext=desktop,NodeTokenSelfObserved=false,Native9Observed=false }); DesktopMatches(desktop,requested); }
                }
                Verify(files); string node=files.Single(f=>f.Observation.Role=="node").Observation.Route;
                var created=Spawn(null,node,new[]{Path.Combine(root,"scripts","next","windows-journal-ci-entry.mjs")},root,Native.GetStdHandle(-10),Native.GetStdHandle(-11),Native.GetStdHandle(-12)); process=created.Process; thread=created.Thread;
                Check(Native.ResumeThread(thread)==1,"NODE_RESUME"); Check(Native.WaitForSingleObject(process,ChildMs)==0,"NODE_EXIT_WAIT"); uint exit; Check(Native.GetExitCodeProcess(process,out exit),"NODE_EXIT_CODE"); Verify(files); Require(exit<=Int32.MaxValue,"NODE_EXIT_RANGE"); return (int)exit;
            } catch(CiFailure error) { if(directory!=null) try { Save(directory,"child-failure.json",new { FailureCode=error.Code,NativeError=error.NativeError }); } catch {} return 1; }
            catch { if(directory!=null) try { Save(directory,"child-failure.json",new { FailureCode="CHILD_EXCEPTION" }); } catch {} return 1; }
            finally { if(process!=null) { if(Native.WaitForSingleObject(process,0)!=0) Native.TerminateProcess(process,1); process.Dispose(); } if(thread!=null) thread.Dispose(); if(files!=null) foreach(var file in files) file.Dispose(); }
        }
        public static LaunchObservation Run() {
            var result=new LaunchObservation(); string root=null,directory=null; List<FileFence> files=null; Handle candidate=null,job=null,process=null,thread=null,inputRead=null,inputWrite=null,outRead=null,outWrite=null,errRead=null,errWrite=null;
            CapturedPipe stdout=null,stderr=null; HandshakeWriter writer=null; uint childTid=0; var timer=Stopwatch.StartNew(); var budget=new OutputBudget();
            try { EnvironmentPreflight(); root=OwningRoot(); files=Fences(root); result.Files=files.Select(f=>f.Observation).ToArray(); result.PowerShellVersion=FileVersionInfo.GetVersionInfo(OwnExecutable()).ProductVersion;
                result.TokenProbe=new ProbeObservation(); Context selected;
                using(var source=OwnToken(false)) result.TokenProbe.SourceContext=Observe(source,Native.GetCurrentProcess(),Native.GetCurrentThread(),Native.GetCurrentProcessId(),"own_process_token");
                // Route selection is complete before launch. There is no retry after an API failure.
                if(IsOrdinary(result.TokenProbe.SourceContext)) { result.LaunchRoute="ordinary-own-process"; selected=result.TokenProbe.SourceContext; }
                else { result.LaunchRoute="restricted-primary"; candidate=Prepare(result.TokenProbe); selected=result.TokenProbe.DerivedContext; }
                result.ArtifactId=Guid.NewGuid().ToString("D"); directory=ArtifactDirectory(root,result.ArtifactId,true);
                result.ReceiptPath="artifacts/next/windows-journal-ci/"+result.ArtifactId+"/launcher.json";
                result.StartupObjects=StartupObjects(candidate,selected);
                job=Native.CreateJobObject(IntPtr.Zero,null); Require(!job.IsInvalid,"JOB_CREATE"); var limits=new ExtendedLimits { Basic=new BasicLimits { Flags=0x2000 } }; Check(Native.SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()),"JOB_LIMITS");
                var sa=new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=0 }; Check(Native.CreatePipe(out inputRead,out inputWrite,ref sa,4096),"INPUT_PIPE"); Check(Native.CreatePipe(out outRead,out outWrite,ref sa,65536),"OUTPUT_PIPE"); Check(Native.CreatePipe(out errRead,out errWrite,ref sa,65536),"ERROR_PIPE");
                Verify(files); string shell=files.Single(f=>f.Observation.Role=="powershell").Observation.Route;
                result.RequestedDesktop=DesktopRoute(OwnDesktop());
                result.StartupSecurity=new StartupSecurityObservation { SelectedTokenDefaultDacl=DefaultDacl(candidate,selected) };
                var created=Spawn(candidate,shell,new[]{"-NoLogo","-NoProfile","-NonInteractive","-File",Path.Combine(root,"scripts","next","windows-journal-ci-child.ps1")},root,inputRead.DangerousGetHandle(),outWrite.DangerousGetHandle(),errWrite.DangerousGetHandle(),result.RequestedDesktop); process=created.Process; thread=created.Thread; result.ChildPid=created.Pid; childTid=created.Tid; result.ChildCreationFiletime=Creation(process.DangerousGetHandle());
                Check(Native.AssignProcessToJobObject(job,process),"JOB_ASSIGN_BEFORE_RESUME"); result.AssignedBeforeResume=true;
                result.StartupSecurity.OwnedChildObjects=new[]{OwnedSecurity(process,"process",result.ChildPid,result.ChildCreationFiletime,childTid),OwnedSecurity(thread,"thread",result.ChildPid,result.ChildCreationFiletime,childTid)};
                inputRead.Dispose(); outWrite.Dispose(); errWrite.Dispose();
                stdout=new CapturedPipe(outRead,Path.Combine(directory,"stdout.log"),budget.Add); stderr=new CapturedPipe(errRead,Path.Combine(directory,"stderr.log"),budget.Add); stdout.Start(); stderr.Start();
                Check(Native.ResumeThread(thread)==1,"CHILD_RESUME");
                byte[] handshake=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { SchemaVersion="bridge-windows-journal-ci-handshake/v2",ArtifactId=result.ArtifactId,SourceContext=result.TokenProbe.SourceContext,SelectedContext=selected,LaunchRoute=result.LaunchRoute,RequestedDesktop=result.RequestedDesktop,Files=result.Files },JsonOptions)); Require(handshake.Length<=65536,"HANDSHAKE_BOUND");
                writer=new HandshakeWriter(inputWrite); writer.Start(handshake);
                while(timer.ElapsedMilliseconds<WorkMs) { uint wait=Native.WaitForSingleObject(process,100); Require(wait==0||wait==258,"CHILD_WAIT"); if(wait==0) { result.ExitObserved=true; break; } Require(!stdout.Failed&&!stderr.Failed&&!writer.Failed&&!budget.Overflow,"CAPTURE_FAILURE"); }
                Require(result.ExitObserved,"LAUNCHER_TIMEOUT"); uint exit; Check(Native.GetExitCodeProcess(process,out exit),"CHILD_EXIT_CODE"); result.ChildExitCode=exit; Require(exit==0,"CHILD_NONZERO");
                // Draining is work too; it cannot consume the cleanup allowance.
                while(timer.ElapsedMilliseconds<WorkMs&&(!stdout.Eof||!stderr.Eof||!writer.Completed||!JobEmpty(job))) Thread.Sleep(20);
                Require(writer.Join(Remaining(timer,WorkMs))&&stdout.Join(Remaining(timer,WorkMs))&&stderr.Join(Remaining(timer,WorkMs)),"IO_THREADS_UNSETTLED");
                result.IoThreadsJoined=true;
                result.OwnedJobEmpty=JobEmpty(job); result.StdoutEof=stdout.Eof; result.StderrEof=stderr.Eof; result.ReaderFailed=stdout.Failed||stderr.Failed;
                result.OutputOverflow=budget.Overflow;
                Require(result.OwnedJobEmpty&&result.StdoutEof&&result.StderrEof&&!result.ReaderFailed&&!writer.Failed&&writer.Completed&&!result.OutputOverflow,"CAPTURE_CUSTODY_UNSETTLED");
                using(var childFile=new FileFence("child-context",Path.Combine(directory,"child-context.json"),65536)) using(var document=JsonDocument.Parse(File.ReadAllBytes(childFile.Observation.Route))) {
                    var child=document.RootElement; Closed(child,new[]{"schemaVersion","context","requestedDesktop","desktopContext","nodeTokenSelfObserved","native9Observed"}); Require(child.GetProperty("schemaVersion").GetString()=="bridge-windows-journal-ci-child-context/v2"&&!child.GetProperty("nodeTokenSelfObserved").GetBoolean()&&!child.GetProperty("native9Observed").GetBoolean(),"CHILD_RECEIPT_SCHEMA");
                    Context observed=ContextFrom(child.GetProperty("context")); Ordinary(observed); SameIdentity(result.TokenProbe.SourceContext,observed); Require(observed.Pid==result.ChildPid&&observed.CreationFiletime==result.ChildCreationFiletime,"CHILD_RECEIPT_PROCESS"); result.ChildContextMatched=true;
                    Require(String.Equals(child.GetProperty("requestedDesktop").GetString(),result.RequestedDesktop,StringComparison.Ordinal),"CHILD_DESKTOP_REQUEST_BINDING"); var desktop=DesktopContextFrom(child.GetProperty("desktopContext")); DesktopMatches(desktop,result.RequestedDesktop); result.ChildDesktopContext=desktop; result.ChildDesktopContextMatched=true; childFile.Verify(); }
                Verify(files); result.SourceToolFenceStable=true; Require(timer.ElapsedMilliseconds<WorkMs,"LAUNCHER_TIMEOUT"); result.CleanupSettled=true; Complete(result); result.Result="passed";
            } catch(CiFailure error) { result.FailureCode=error.Code; result.NativeError=error.NativeError; if(result.LaunchRoute=="restricted-primary"&&result.TokenProbe!=null&&result.TokenProbe.DerivedAttempted&&!result.TokenProbe.DerivedPredicateAccepted) { result.TokenProbe.FailureCode=error.Code; result.TokenProbe.NativeError=error.NativeError; } }
            catch { result.FailureCode="LAUNCHER_EXCEPTION"; }
            finally {
                if(result.Result!="passed"&&process!=null) { result.ForcedCleanup=true; result.CleanupKillApiSucceeded=job!=null&&!job.IsInvalid&&result.AssignedBeforeResume?Native.TerminateJobObject(job,1):Native.TerminateProcess(process,1);
                    if(writer!=null) writer.Cancel(); else if(inputWrite!=null) inputWrite.Dispose();
                    long cleanupStart=timer.ElapsedMilliseconds,cleanupEnd=CleanupDeadline(cleanupStart); bool readersCancelled=false;
                    try { for(;;) {
                            // Even an expired deadline must request cancellation and
                            // observe owned custody once without a blocking wait.
                            if(!readersCancelled&&ReaderCancellationDue(timer.ElapsedMilliseconds,cleanupStart,cleanupEnd)) { if(stdout!=null) stdout.Cancel(); if(stderr!=null) stderr.Cancel(); readersCancelled=true; }
                            bool exited=Native.WaitForSingleObject(process,0)==0; bool empty=job==null||job.IsInvalid||JobEmpty(job);
                            bool stdoutJoined=stdout==null||stdout.Join(0),stderrJoined=stderr==null||stderr.Join(0),writerJoined=writer==null||writer.Join(0);
                            if(exited&&empty&&stdoutJoined&&stderrJoined&&writerJoined) { result.CleanupSettled=true; result.IoThreadsJoined=true; break; }
                            if(timer.ElapsedMilliseconds>=cleanupEnd) break;
                            Thread.Sleep(Math.Min(20,Remaining(timer,cleanupEnd))); } } catch { result.CleanupSettled=false; }
                }
                if(stdout!=null) { result.StdoutEof=stdout.Eof; result.ReaderFailed|=stdout.Failed; } if(stderr!=null) { result.StderrEof=stderr.Eof; result.ReaderFailed|=stderr.Failed; }
                result.OutputOverflow=budget.Overflow;
                if(job!=null&&!job.IsInvalid) try { result.OwnedJobEmpty=JobEmpty(job); } catch { result.CleanupSettled=false; }
                if(files!=null) { try { Verify(files); result.SourceToolFenceStable=true; } catch { result.Result="failed"; result.SourceToolFenceStable=false; result.FailureCode="FILE_FENCE_CHANGED"; } foreach(var file in files) file.Dispose(); }
                if(stdout!=null) stdout.CloseUnstarted(); if(stderr!=null) stderr.CloseUnstarted(); if(writer!=null) writer.CloseUnstarted();
                foreach(var handle in new[]{candidate,process,thread,inputRead,writer==null?inputWrite:null,outWrite,errWrite,job}) if(handle!=null) handle.Dispose();
                if(stdout==null&&outRead!=null) outRead.Dispose(); if(stderr==null&&errRead!=null) errRead.Dispose(); result.CompletedAt=DateTime.UtcNow.ToString("O");
                FitStartupSecurity(result,childTid);
                if(directory!=null) try { Save(directory,"launcher.json",result); } catch { result.Result="failed"; result.FailureCode="RECEIPT_WRITE_FAILED"; }
            }
            return result;
        }
        public static string[] ControlTests() {
            var passed=new List<string>(); Action<string,Action> refuses=(name,action)=> { try { action(); } catch(CiFailure) { passed.Add(name); return; } throw new Exception("Expected refusal: "+name); };
            Func<DesktopContext> desktop=()=>new DesktopContext { WindowStationName="WinSta0",ThreadDesktopName="Default" };
            Require(DesktopRoute(desktop())=="WinSta0\\Default","DESKTOP_ROUTE_TEST"); passed.Add("own station and desktop compose one explicit route");
            DesktopMatches(desktop(),"winsta0\\default"); passed.Add("desktop names match ignoring ordinal case");
            var maximum=desktop(); maximum.WindowStationName=new string('w',512); maximum.ThreadDesktopName=new string('d',512); DesktopRequest(DesktopRoute(maximum)); passed.Add("maximum bounded components retain one separator");
            foreach(string bad in new[]{null,"",new string('x',513),"embedded\0name","station\\desktop","station/desktop","line\nbreak","unpaired\ud800"}) {
                refuses("invalid station component "+passed.Count,()=>{var own=desktop(); own.WindowStationName=bad; DesktopRoute(own);});
                refuses("invalid desktop component "+passed.Count,()=>{var own=desktop(); own.ThreadDesktopName=bad; DesktopRoute(own);}); }
            foreach(string bad in new[]{null,"","WinSta0","\\Default","WinSta0\\","WinSta0\\Default\\extra"}) refuses("invalid desktop request "+passed.Count,()=>DesktopRequest(bad));
            refuses("child station mismatch",()=>{var own=desktop(); own.WindowStationName="other"; DesktopMatches(own,"WinSta0\\Default");});
            refuses("child desktop mismatch",()=>{var own=desktop(); own.ThreadDesktopName="other"; DesktopMatches(own,"WinSta0\\Default");});
            foreach(string json in new[]{"{\"observationKind\":\"parent\",\"windowStationName\":\"WinSta0\",\"threadDesktopName\":\"Default\"}","{\"observationKind\":\"own_process_window_station_and_current_thread_desktop\",\"windowStationName\":\"WinSta0\"}","{\"observationKind\":\"own_process_window_station_and_current_thread_desktop\",\"windowStationName\":\"WinSta0\",\"threadDesktopName\":\"Default\",\"extra\":true}"}) refuses("closed child desktop observation "+passed.Count,()=>{using(var parsed=JsonDocument.Parse(json)) DesktopContextFrom(parsed.RootElement);});
            Require(ChildMs<WorkMs&&TotalMs-WorkMs>=CleanupMs,"CLEANUP_RESERVE"); passed.Add("work and drain reserve the fixed cleanup allowance");
            Require(CleanupDeadline(1000)==31000&&CleanupDeadline(WorkMs)==TotalMs&&CleanupDeadline(TotalMs+1)==TotalMs,"CLEANUP_DEADLINE"); passed.Add("early, work-horizon and expired cleanup deadlines stay bounded");
            Require(!ReaderCancellationDue(670499,670000,TotalMs)&&ReaderCancellationDue(670500,670000,TotalMs),"READER_GRACE"); passed.Add("reader settlement grace ends at 500 milliseconds");
            Require(ReaderCancellationDue(TotalMs,TotalMs,TotalMs)&&ReaderCancellationDue(TotalMs+1,TotalMs+1,TotalMs)&&ReaderCancellationDue(TotalMs,699500,TotalMs),"EXPIRED_READER_CANCEL"); passed.Add("expired or grace-exhausted cleanup immediately requests reader cancellation");
            Func<Context> ordinary=()=>new Context { TokenType=1,Elevated=false,ElevationType=1,IntegrityRid=8192,IntegrityAttributes=0x20,ThreadTokenAbsent=true,ProcessMachine=0,NativeMachine=0x8664,UserSid="S-1-5-21-1",SessionId=1,AuthenticationId="0000000000000001",LocalAppData="C:\\Users\\ordinary\\AppData\\Local" };
            Ordinary(ordinary()); passed.Add("ordinary predicate accepts default token"); var limited=ordinary(); limited.ElevationType=3; Ordinary(limited); passed.Add("ordinary predicate accepts limited token");
            var mutations=new Action<Context>[] {c=>c.TokenType=2,c=>c.Elevated=true,c=>c.ElevationType=2,c=>c.IntegrityRid=4096,c=>c.IntegrityRid=12288,c=>c.IntegrityAttributes=0,c=>c.ThreadTokenAbsent=false,c=>c.ProcessMachine=0x14c,c=>c.NativeMachine=0xaa64};
            for(int i=0;i<mutations.Length;i++) { int index=i; refuses("required context field "+i,()=>{var c=ordinary(); mutations[index](c); Ordinary(c);}); }
            var identityChanges=new Action<Context>[] {c=>c.UserSid="S-1-5-21-2",c=>c.SessionId=2,c=>c.AuthenticationId="0000000000000002",c=>c.LocalAppData="C:\\Users\\other\\AppData\\Local"};
            for(int i=0;i<identityChanges.Length;i++) { int index=i; refuses("same-user identity field "+i,()=>{var c=ordinary(); identityChanges[index](c); SameIdentity(ordinary(),c);}); }
            foreach(string json in new[]{"{}","[]","{\"schemaVersion\":1,\"schemaVersion\":2}","{\"schemaVersion\":1,\"extra\":2}"}) refuses("closed duplicate/unknown handshake "+json,()=>{using(var parsed=JsonDocument.Parse(json)) Closed(parsed.RootElement,new[]{"schemaVersion"});});
            refuses("artifact traversal",()=>ArtifactDirectory("C:\\", "..",false)); refuses("artifact uppercase or brace alias",()=>ArtifactDirectory("C:\\","{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}",false));
            Require(Quote("C:\\path with spaces\\tail\\") == "\"C:\\path with spaces\\tail\\\\\"","QUOTE_TRAILING_SLASH_TEST"); passed.Add("quoted trailing slash cannot consume closing quote");
            Require(Quote("a\"b")=="\"a\\\"b\"","QUOTE_INTERIOR_TEST"); passed.Add("interior quote stays one argument"); refuses("command newline",()=>Quote("a\nb"));
            var budget=new OutputBudget(); for(int i=0;i<1024;i++) budget.Add(65536); Require(!budget.Overflow,"EXACT_CAPTURE_BOUND"); passed.Add("exact combined capture cap is representable");
            refuses("combined stdout/stderr next byte overflows",()=>budget.Add(1)); Require(budget.Overflow,"OVERFLOW_LATCH");
            refuses("capture block cannot bypass fixed reader allocation",()=>new OutputBudget().Add(65537));
            Func<LaunchObservation> settled=()=>new LaunchObservation { AssignedBeforeResume=true,ChildContextMatched=true,ChildDesktopContextMatched=true,ExitObserved=true,ChildExitCode=0,StdoutEof=true,StderrEof=true,
                IoThreadsJoined=true,OwnedJobEmpty=true,SourceToolFenceStable=true,CleanupSettled=true };
            Complete(settled()); passed.Add("only complete custody is eligible for success");
            var qualified=settled(); qualified.Result="passed";
            Require(!qualified.PackageAcceptance&&!qualified.FullBr06Accepted&&!qualified.NativeRuntimeQualified&&!qualified.ReleaseQualified,"QUALIFICATION_NOT_GRANTED");
            passed.Add("passed helper custody grants no package, campaign, native or release qualification");
            var custodyChanges=new Action<LaunchObservation>[] {c=>c.AssignedBeforeResume=false,c=>c.ChildContextMatched=false,c=>c.ChildDesktopContextMatched=false,c=>c.ExitObserved=false,c=>c.ChildExitCode=1,
                c=>c.StdoutEof=false,c=>c.StderrEof=false,c=>c.IoThreadsJoined=false,c=>c.OwnedJobEmpty=false,c=>c.SourceToolFenceStable=false,c=>c.CleanupSettled=false,
                c=>c.ReaderFailed=true,c=>c.OutputOverflow=true,c=>c.ForcedCleanup=true,c=>c.FailureCode="TIMEOUT"};
            for(int i=0;i<custodyChanges.Length;i++) { int index=i; refuses("zero exit cannot rehabilitate missing custody "+i,()=>{var c=settled(); custodyChanges[index](c); Complete(c);}); }
            // Synthetic bytes exercise parsing and bounds only. No native query,
            // process creation, access decision or security mutation occurs here.
            Func<int,int,byte[]> acl=(bytes,count)=> { var data=new byte[bytes]; data[0]=2; Array.Copy(BitConverter.GetBytes((ushort)bytes),0,data,2,2); Array.Copy(BitConverter.GetBytes((ushort)count),0,data,4,2);
                if(count==1&&bytes>=28) { data[10]=20; data[16]=1; data[17]=1; data[23]=5; data[24]=32; } return data; };
            Require(AclBytes(acl(8,0))=="empty","SECURITY_EMPTY_ACL_TEST"); passed.Add("zero ACEs are an empty ACL rather than NULL");
            Require(AclBytes(acl(28,1))=="populated","SECURITY_POPULATED_ACL_TEST"); passed.Add("one bounded ACE is a populated ACL");
            var freeAcl=acl(SecurityBytes,0); freeAcl[0]=4; Require(AclBytes(freeAcl)=="empty","SECURITY_ACL_FREE_SPACE_TEST"); passed.Add("ACL revision four and bounded free space are retained");
            refuses("ACL header shorter than eight bytes",()=>AclBytes(new byte[7])); refuses("ACL next byte exceeds snapshot cap",()=>AclBytes(new byte[SecurityBytes+1]));
            var badAclChanges=new Action<byte[]>[] {c=>c[0]=1,c=>c[2]=27,c=>c[4]=129,c=>c[10]=0,c=>c[10]=6,c=>c[10]=24,c=>c[4]=2};
            for(int i=0;i<badAclChanges.Length;i++) { int index=i; refuses("ACL revision, size, count or ACE extent "+i,()=>{var data=acl(28,1); badAclChanges[index](data); AclBytes(data);}); }
            SecurityRange(100,(uint)(IntPtr.Size+8),(ulong)(100+IntPtr.Size),(uint)IntPtr.Size,8); passed.Add("PACL starts after the pointer header and ends at returned extent");
            var badRanges=new Action[] {()=>SecurityRange(100,16,99,8,8),()=>SecurityRange(100,16,100,8,8),()=>SecurityRange(100,16,109,8,8),()=>SecurityRange(100,7,108,8,8),
                ()=>SecurityRange(100,(uint)(SecurityBytes+IntPtr.Size+1),108,8,8),()=>SecurityRange(UInt64.MaxValue-7,16,UInt64.MaxValue-7,0,8),()=>SecurityRange(100,16,108,8,7)};
            for(int i=0;i<badRanges.Length;i++) { int index=i; refuses("borrowed PACL pointer containment or overflow "+i,badRanges[index]); }
            Func<string,byte[]> descriptor=state=> { byte[] dacl=state=="empty"?acl(8,0):state=="populated"?acl(28,1):null; var data=new byte[20+(dacl==null?0:dacl.Length)]; data[0]=1; data[3]=0x80;
                if(state!="absent") data[2]=4; if(dacl!=null) { data[16]=20; Array.Copy(dacl,0,data,20,dacl.Length); } return data; };
            foreach(string state in new[]{"absent","null","empty","populated"}) { Require(DescriptorBytes(descriptor(state))==state,"SECURITY_DESCRIPTOR_STATE_TEST"); passed.Add("self-relative descriptor keeps DACL state "+state); }
            refuses("security descriptor short header",()=>DescriptorBytes(new byte[19])); refuses("security descriptor next byte exceeds cap",()=>DescriptorBytes(new byte[SecurityBytes+1]));
            var badDescriptorChanges=new Action<byte[]>[] {c=>c[0]=0,c=>c[3]=0,c=>c[2]|=0x10,c=>c[12]=20,c=>c[16]=16,c=>c[16]=21,c=>c[16]=44,
                c=>Array.Copy(BitConverter.GetBytes(UInt32.MaxValue),0,c,16,4),c=>c[22]=255,c=>c[4]=20,c=>c[8]=47,c=>c[2]=0};
            for(int i=0;i<badDescriptorChanges.Length;i++) { int index=i; refuses("self-relative flags, SACL, SID or DACL offset "+i,()=>{var data=descriptor("populated"); badDescriptorChanges[index](data); DescriptorBytes(data);}); }
            var nullDefault=new TokenDefaultDaclObservation(); SecurityObserved(nullDefault,null,"null"); SecurityPayload(nullDefault,true); passed.Add("NULL token default DACL has no fabricated ACL bytes or hash");
            var unavailable=new TokenDefaultDaclObservation(); SecurityUnavailable(unavailable,"QUERY_REFUSED",UInt32.MaxValue); SecurityPayload(unavailable,true); Require(unavailable.NativeError==UInt32.MaxValue,"SECURITY_DIRECT_DWORD_TEST"); passed.Add("unavailable observation retains a direct unsigned native error");
            var emptyDefault=new TokenDefaultDaclObservation(); SecurityObserved(emptyDefault,acl(8,0),"empty"); SecurityPayload(emptyDefault,true); passed.Add("empty token default DACL requires exact bounded bytes and hash");
            var badPayloadChanges=new Action<TokenDefaultDaclObservation>[] {c=>c.Result="unsupported",c=>c.FailureCode="QUERY_REFUSED",c=>c.NativeError=5,c=>c.AclState="null",c=>c.Bytes=9,
                c=>c.Bytes=(uint)(SecurityBytes+1),c=>c.DataBase64="*",c=>c.Sha256=new string('0',64),c=>c.AclState="populated"};
            for(int i=0;i<badPayloadChanges.Length;i++) { int index=i; refuses("security payload state, extent, encoding or hash "+i,()=>{var value=new TokenDefaultDaclObservation(); SecurityObserved(value,acl(8,0),"empty"); badPayloadChanges[index](value); SecurityPayload(value,true);}); }
            refuses("unavailable observation cannot carry observed data",()=>{var value=new TokenDefaultDaclObservation(); SecurityUnavailable(value,"QUERY_REFUSED"); value.DataBase64="AA=="; SecurityPayload(value,true);});
            refuses("security payload cannot be missing",()=>SecurityPayload(null,true));
            Func<StartupSecurityObservation> security=()=> { var value=new StartupSecurityObservation { SelectedTokenDefaultDacl=new TokenDefaultDaclObservation { SelectedTokenObservationKind="own_process_token",ReturnedBytes=(uint)(IntPtr.Size+8) },
                    OwnedChildObjects=new[]{new OwnedChildSecurityObservation { Role="process",Pid=11,CreationFiletime="12",InitialThreadId=13 },new OwnedChildSecurityObservation { Role="thread",Pid=11,CreationFiletime="12",InitialThreadId=13 }} };
                SecurityObserved(value.SelectedTokenDefaultDacl,acl(8,0),"empty"); foreach(var item in value.OwnedChildObjects) SecurityObserved(item,descriptor("empty"),"empty"); return value; };
            Action<StartupSecurityObservation,string,bool> parseSecurity=(value,kind,assigned)=> { using(var parsed=JsonDocument.Parse(JsonSerializer.Serialize(value,JsonOptions))) SecurityFrom(parsed.RootElement,kind,11,"12",13,assigned); };
            parseSecurity(security(),"own_process_token",true); passed.Add("closed security bundle binds process and thread to the returned child identity");
            var derivedSecurity=security(); derivedSecurity.SelectedTokenDefaultDacl.SelectedTokenObservationKind="derived_token_only"; parseSecurity(derivedSecurity,"derived_token_only",true); passed.Add("derived default DACL binds to the selected candidate route");
            var beforeAssignment=security(); beforeAssignment.OwnedChildObjects=null; parseSecurity(beforeAssignment,"own_process_token",false); passed.Add("no owned-child snapshots are claimed before Job assignment");
            var badSecurityChanges=new Action<StartupSecurityObservation>[] {c=>c.SchemaVersion="other",c=>c.SelectedTokenDefaultDacl.ObservationKind="parent",c=>c.SelectedTokenDefaultDacl.SelectedTokenObservationKind="derived_token_only",
                c=>c.SelectedTokenDefaultDacl.TokenInformationClass=5,c=>c.SelectedTokenDefaultDacl.ReturnedBytes=(uint)IntPtr.Size,c=>c.OwnedChildObjects=null,c=>c.OwnedChildObjects=c.OwnedChildObjects.Take(1).ToArray(),
                c=>Array.Reverse(c.OwnedChildObjects),c=>c.OwnedChildObjects[0].Role="other",c=>c.OwnedChildObjects[0].Phase="after_resume",c=>c.OwnedChildObjects[0].Pid=12,
                c=>c.OwnedChildObjects[0].CreationFiletime="13",c=>c.OwnedChildObjects[1].InitialThreadId=14,c=>c.OwnedChildObjects[0].ObjectType=1,c=>c.OwnedChildObjects[1].RequestedInformation=4};
            for(int i=0;i<badSecurityChanges.Length;i++) { int index=i; refuses("security bundle schema, route, role, phase or returned identity "+i,()=>{var value=security(); badSecurityChanges[index](value); parseSecurity(value,"own_process_token",true);}); }
            refuses("owned-child snapshots require Job assignment",()=>parseSecurity(security(),"own_process_token",false));
            string securityJson=JsonSerializer.Serialize(security(),JsonOptions);
            foreach(string json in new[]{securityJson.Replace("\"ownedChildObjects\":","\"extra\": true, \"ownedChildObjects\":"),securityJson.Replace("\"schemaVersion\":","\"schemaVersion\": \"duplicate\", \"schemaVersion\":"),securityJson.Replace("\"schemaVersion\"","\"missingSchema\"")})
                refuses("closed security bundle unknown, duplicate or missing field "+passed.Count,()=>{using(var parsed=JsonDocument.Parse(json)) SecurityFrom(parsed.RootElement,"own_process_token",11,"12",13,true);});
            foreach(string json in new[]{securityJson.Replace("\"tokenInformationClass\":","\"extra\": true, \"tokenInformationClass\":"),securityJson.Replace("\"objectType\":","\"extra\": true, \"objectType\":")})
                refuses("closed token and owned-object security records "+passed.Count,()=>{using(var parsed=JsonDocument.Parse(json)) SecurityFrom(parsed.RootElement,"own_process_token",11,"12",13,true);});
            refuses("NULL token default requires pointer-sized returned data",()=>{var value=security(); SecurityObserved(value.SelectedTokenDefaultDacl,null,"null"); value.SelectedTokenDefaultDacl.ReturnedBytes=(uint)(IntPtr.Size+1); parseSecurity(value,"own_process_token",true);});
            SecurityBudget(SecurityDiagnosticBytes,ReceiptBytes); passed.Add("exact optional-diagnostic and aggregate receipt caps are accepted");
            refuses("next optional-diagnostic byte exceeds cap",()=>SecurityBudget(SecurityDiagnosticBytes+1,ReceiptBytes)); refuses("next aggregate receipt byte exceeds cap",()=>SecurityBudget(SecurityDiagnosticBytes,ReceiptBytes+1));
            refuses("negative serialized byte count is invalid",()=>SecurityBudget(-1,0));
            Func<LaunchObservation> securityLaunch=()=> { var value=settled(); value.Result="passed"; value.LaunchRoute="ordinary-own-process"; value.ChildPid=11; value.ChildCreationFiletime="12"; value.StartupSecurity=security(); return value; };
            var optional=securityLaunch(); SecurityUnavailable(optional.StartupSecurity.SelectedTokenDefaultDacl,"QUERY_REFUSED",5); foreach(var item in optional.StartupSecurity.OwnedChildObjects) SecurityUnavailable(item,"QUERY_REFUSED",5);
            FitStartupSecurity(optional,13); Complete(optional); Require(optional.Result=="passed"&&optional.StartupSecurity.SelectedTokenDefaultDacl.NativeError==5&&!optional.NativeRuntimeQualified,"SECURITY_OPTIONAL_OUTCOME_TEST"); passed.Add("unavailable startup observations preserve launch custody and qualification boundaries");
            var crowded=securityLaunch(); crowded.PowerShellVersion=""; crowded.PowerShellVersion=new string('x',ReceiptBytes-SerializedBytes(crowded)+1); FitStartupSecurity(crowded,13); Complete(crowded);
            Require(crowded.Result=="passed"&&crowded.StartupSecurity!=null&&crowded.StartupSecurity.SelectedTokenDefaultDacl.Result=="unavailable"&&SerializedBytes(crowded)<=ReceiptBytes,"SECURITY_OPTIONAL_BUDGET_TEST"); passed.Add("optional raw snapshots are discarded before aggregate receipt overflow changes outcome");
            var oversizedBaseline=securityLaunch(); oversizedBaseline.PowerShellVersion=new string('x',ReceiptBytes); FitStartupSecurity(oversizedBaseline,13); Complete(oversizedBaseline);
            Require(oversizedBaseline.Result=="passed"&&oversizedBaseline.StartupSecurity==null&&SerializedBytes(oversizedBaseline)>ReceiptBytes,"SECURITY_BASELINE_BOUND_TEST"); passed.Add("diagnostic omission does not relax the original fail-closed aggregate receipt cap");
            var malformedOptional=securityLaunch(); malformedOptional.StartupSecurity.OwnedChildObjects[0]=null; FitStartupSecurity(malformedOptional,13); Complete(malformedOptional);
            Require(malformedOptional.Result=="passed"&&malformedOptional.StartupSecurity==null,"SECURITY_OPTIONAL_EXCEPTION_TEST"); passed.Add("optional diagnostic normalization failure preserves the launch result");
            return passed.ToArray();
        }
    }
}
