﻿using System;
using System.Collections.Generic;
using System.IO;
using Ionic.Zlib;

using ThomasJepp.SaintsRow.AssetAssembler;

namespace ThomasJepp.SaintsRow.Packfiles.Version04
{
    public class Packfile : IPackfile
    {
        private List<IPackfileEntry> m_Files;
        private PackfileFileData FileData;

        private Dictionary<string, Stream> m_Streams;

        public Stream DataStream;

        public Packfile()
        {
            FileData = new PackfileFileData();
            m_Files = new List<IPackfileEntry>();
            m_Streams = new Dictionary<string, Stream>();
        }

        public Packfile(Stream stream)
        {
            DataStream = stream;

            stream.Seek(0, SeekOrigin.Begin);
            FileData = stream.ReadStruct<PackfileFileData>();

            m_Files = new List<IPackfileEntry>();

            stream.Seek(GetEntryDataOffset(), SeekOrigin.Begin);

            List<PackfileEntryFileData> entryFileData = new List<PackfileEntryFileData>();

            for (int i = 0; i < FileData.IndexCount; i++)
            {
                var fileData = stream.ReadStruct<PackfileEntryFileData>();
                entryFileData.Add(fileData);
            }

            List<string> fileNames = new List<string>();
            for (int i = 0; i < FileData.IndexCount; i++)
            {
                var fileData = entryFileData[i];
                stream.Seek(CalculateEntryNamesOffset() + fileData.FilenameOffset, SeekOrigin.Begin);
                string name = stream.ReadAsciiNullTerminatedString();

                stream.Seek(CalculateExtensionsOffset() + fileData.ExtensionOffset, SeekOrigin.Begin);
                string extension = stream.ReadAsciiNullTerminatedString();

                m_Files.Add(new PackfileEntry(this, fileData, name + "." + extension));
            }
        }
        public void Dispose()
        {
            DataStream.Dispose();
        }

        public List<IPackfileEntry> Files
        {
            get { return m_Files; }
        }

        public IPackfileEntry this[int i]
        {
            get { return m_Files[i]; }
        }

        public IPackfileEntry this[string s]
        {
            get
            {
                string lowercase = s.ToLowerInvariant();
                foreach (IPackfileEntry entry in m_Files)
                {
                    if (entry.Name.ToLowerInvariant() == lowercase)
                        return entry;
                }

                return null;
            }
        }

        public int Version
        {
            get
            {
                return 0x04;
            }
        }


        public bool IsCompressed
        {
            get { return FileData.Flags.HasFlag(PackfileFlags.Compressed); }
            set { if (value) { FileData.Flags |= PackfileFlags.Compressed; } else { FileData.Flags &= ~PackfileFlags.Compressed; } }
        }

        public bool IsCondensed
        {
            get { return FileData.Flags.HasFlag(PackfileFlags.Condensed); }
            set { if (value) { FileData.Flags |= PackfileFlags.Condensed; } else { FileData.Flags &= ~PackfileFlags.Condensed; } }
        }


        public void AddFile(Stream stream, string filename)
        {
            Files.Add(new PackfileEntry(this, new PackfileEntryFileData(), filename, true));
            m_Streams.Add(filename, stream);
        }

        private long GetEntryDataOffset()
        {
            return (0x180).Align(2048);
        }

        private long CalculateEntryNamesOffset()
        {
            return (GetEntryDataOffset() + FileData.IndexSize).Align(2048);
        }

        private long CalculateExtensionsOffset()
        {
            return (CalculateEntryNamesOffset() + FileData.NamesSize).Align(2048);
        }

        internal long CalculateDataStartOffset()
        {
            return (CalculateExtensionsOffset() + FileData.ExtensionsSize).Align(2048);
        }

        public void Save(Stream stream)
        {
            // Calculate IndexSize
            FileData.IndexCount = (uint)Files.Count;
            FileData.IndexSize = (FileData.IndexCount * 0x1C);

            // Write Names & calculate NamesSize
            Dictionary<string, uint> filenames = new Dictionary<string, uint>(StringComparer.InvariantCultureIgnoreCase);
            stream.Seek(CalculateEntryNamesOffset(), SeekOrigin.Begin);
            uint filenameOffset = 0;
            foreach (PackfileEntry entry in Files)
            {
                string filename = Path.GetFileNameWithoutExtension(entry.Name);

                if (filenames.ContainsKey(filename))
                {
                    entry.Data.FilenameOffset = filenames[filename];
                }
                else
                {
                    entry.Data.FilenameOffset = filenameOffset;
                    int length = stream.WriteAsciiNullTerminatedString(filename);
                    filenames.Add(filename, filenameOffset);
                    filenameOffset += (uint)length;
                }

            }
            FileData.NamesSize = filenameOffset;
            
            // Write Extensions & calculate ExtensionsSize
            Dictionary<string, uint> extensions = new Dictionary<string, uint>(StringComparer.InvariantCultureIgnoreCase);
            uint extensionOffset = 0;
            stream.Seek(CalculateExtensionsOffset(), SeekOrigin.Begin);
            foreach (PackfileEntry entry in Files)
            {
                string extension = Path.GetExtension(entry.Name);
                if (extension.StartsWith("."))
                    extension = extension.Remove(0, 1);

                if (extensions.ContainsKey(extension))
                {
                    entry.Data.ExtensionOffset = extensions[extension];
                }
                else
                {
                    entry.Data.ExtensionOffset = extensionOffset;
                    int length = stream.WriteAsciiNullTerminatedString(extension);
                    extensions.Add(extension, extensionOffset);
                    extensionOffset += (uint)length;
                }
            }
            FileData.ExtensionsSize = extensionOffset;

            // Write data
            uint dataOffset = 0;
            uint uncompressedSize = 0;
            stream.Seek(CalculateDataStartOffset(), SeekOrigin.Begin);
            foreach (PackfileEntry entry in Files)
            {
                Console.WriteLine();
                Console.WriteLine("Before file {0}, stream pos {1}, dataOffset {2}", entry.Name, stream.Position, dataOffset);

                Stream inStream = m_Streams[entry.Name];
                entry.Data.Size = (uint)inStream.Length;
                entry.Data.Start = dataOffset;

                if (IsCompressed)
                {
                    long beforeData = stream.Position;
                    using (ZlibStream zs = new ZlibStream(stream, CompressionMode.Compress, CompressionLevel.BestCompression, true))
                    {
                        zs.FlushMode = FlushType.Sync;
                        inStream.CopyTo(zs);

                        zs.Flush();
                    }
                    long afterData = stream.Position;
                    entry.Data.CompressedSize = (uint)(afterData - beforeData);
                    dataOffset += entry.Data.CompressedSize;
                    uncompressedSize += entry.Data.Size;
                    uncompressedSize = uncompressedSize.Align(2048);
                }
                else
                {
                    entry.Data.CompressedSize = (uint)0xFFFFFFFF;
                    inStream.CopyTo(stream);
                    dataOffset += entry.Data.Size;
                }

                Console.WriteLine("Wrote file {0}, stream pos {1}, dataOffset {2}", entry.Name, stream.Position, dataOffset);

                stream.Align(2048);
                dataOffset = dataOffset.Align(2048);

                Console.WriteLine("Wrote file {0}, AFTER ALIGN stream pos {1}, dataOffset {2}", entry.Name, stream.Position, dataOffset);
            }

            stream.SetLength(stream.Position);

            // Write Header
            stream.Seek(0, SeekOrigin.Begin);
            FileData.Descriptor = 0x51890ACE;
            FileData.Version = 0x04;
            FileData.CompressedDataSize = IsCompressed ? dataOffset : 0xFFFFFFFF;
            FileData.UncompressedDataSize = IsCompressed ? uncompressedSize : dataOffset;
            FileData.PackageSize = (uint)stream.Length;
            stream.WriteStruct(FileData);
            
            // Write file index
            stream.Seek(GetEntryDataOffset(), SeekOrigin.Begin);
            foreach (PackfileEntry entry in Files)
            {
                stream.WriteStruct(entry.Data);
            }

            Console.WriteLine("Stream length: {0}", (uint)stream.Length);
        }


        public void Update(IContainer container)
        {
            throw new NotImplementedException("Saints Row 2 does not have ASM files.");
        }

        public bool ContainsFile(string filename)
        {
            foreach (PackfileEntry entry in Files)
            {
                if (entry.Name == filename)
                {
                    return true;
                }
            }

            return false;
        }

        public void RemoveFile(string filename)
        {
            PackfileEntry entry = null;

            foreach (PackfileEntry e in Files)
            {
                if (e.Name == filename)
                {
                    entry = e;
                    break;
                }
            }

            if (entry == null)
                return;

            RemoveFile(entry);
        }

        public void RemoveFile(IPackfileEntry entry)
        {
            Files.Remove(entry);
            if (m_Streams != null && m_Streams.ContainsKey(entry.Name))
                m_Streams.Remove(entry.Name);
        }
    }
}
