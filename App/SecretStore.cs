using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NaoOpenAIChat;

// DPAPI defaults to the current Windows user. No machine-wide protection or plaintext fallback.
public sealed class SecretStore(string folder)
{
    private string SecretPath => Path.Combine(folder, "openai-key.dat");
    public bool Exists => File.Exists(SecretPath);
    public void Save(string key)
    {
        Validate(key);
        Directory.CreateDirectory(folder);
        var bytes = Encoding.UTF8.GetBytes(key);
        try
        {
            var protectedBytes = Transform(bytes, true);
            var temporary = SecretPath + ".new-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temporary, protectedBytes); File.Move(temporary, SecretPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string? Load()
    {
        if (!Exists) return null;
        var clear = Transform(File.ReadAllBytes(SecretPath), false);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Forget() { if (Exists) File.Delete(SecretPath); }
    public static void Validate(string key)
    {
        if (key.Length is < 20 or > 1024 || key.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Paste the complete OpenAI API key, without spaces or line breaks.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect
                ? CryptProtectData(ref input, "NAO English Chat OpenAI key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows could not protect or unlock the saved API key for this account.", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) { Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
        }
    }
}
