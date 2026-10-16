using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace LocalMorph.Packaging;

public static class StoreSymbols
{
    private static readonly object NativeLock = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SymbolIndex
    {
        public uint StructureSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string File;
        [MarshalAs(UnmanagedType.Bool)] public bool Stripped;
        public uint Timestamp;
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string DebugFile;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string PdbFile;
        public Guid Guid;
        public uint Signature;
        public uint Age;
    }

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymSrvGetFileIndexInfoW(string file, ref SymbolIndex info, uint flags);

    public static void AssertMatch(string binary, string pdb)
    {
        using var image = File.OpenRead(binary);
        using var pe = new PEReader(image);
        var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException($"Expected one CodeView symbol identity in {Path.GetFileName(binary)}.");
        var codeView = pe.ReadCodeViewDebugDirectoryData(entries[0]);
        if (!string.Equals(Path.GetFileName(codeView.Path), Path.GetFileName(pdb), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PDB filename does not match {Path.GetFileName(binary)}.");

        using var symbols = File.OpenRead(pdb);
        var signature = new byte[4];
        if (symbols.Read(signature, 0, signature.Length) != signature.Length)
            throw new InvalidDataException($"Empty or truncated symbols: {Path.GetFileName(pdb)}.");
        symbols.Position = 0;
        if (signature[0] == 'B' && signature[1] == 'S' && signature[2] == 'J' && signature[3] == 'B')
        {
            using var provider = MetadataReaderProvider.FromPortablePdbStream(symbols);
            var header = provider.GetMetadataReader().DebugMetadataHeader;
            if (header == null)
                throw new InvalidDataException($"Missing portable PDB identity: {Path.GetFileName(pdb)}.");
            var identity = new BlobContentId(header.Id);
            if (identity.Guid != codeView.Guid || identity.Stamp != entries[0].Stamp || codeView.Age != 1)
                throw new InvalidDataException($"Portable symbols do not match {Path.GetFileName(binary)}.");
        }
        else
        {
            var identity = new SymbolIndex { StructureSize = (uint)Marshal.SizeOf<SymbolIndex>() };
            lock (NativeLock)
            {
                if (!SymSrvGetFileIndexInfoW(Path.GetFullPath(pdb), ref identity, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot read native symbols: {Path.GetFileName(pdb)}.");
            }
            if (identity.Guid == Guid.Empty || identity.Guid != codeView.Guid || identity.Age != codeView.Age)
                throw new InvalidDataException($"Native symbols do not match {Path.GetFileName(binary)}.");
        }
    }
}
