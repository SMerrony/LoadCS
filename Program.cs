/*
MIT License
Copyright (c) 2019 Stephen Merrony
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.IO;

namespace LoadCS
{
    // Aliases for DG-standard types
    using DgByte = System.Byte;
    using DgWord = System.UInt16;
    using DgDword = System.UInt32;

    class Program
    {
        const string semVer = "v0.9.4";

        static void Main(string[] args)
        {
            string dump = "";
            bool extract = false, ignoreErrors = false, list = false, summary = false, verbose = false;
            AosvsDumpFile dumpFile;

            if (args.Length == 0)
            {
                Console.WriteLine("ERROR: No arguments supplied.");
                PrintHelp();
            }
            foreach (string str in args)
            {
                string[] strs = str.Split('=');
                switch (strs[0])
                {
                    case "--dumpfile":
                        dump = strs[1];
                        break;
                    case "--extract":
                        extract = true;
                        break;
                    case "--help":
                        PrintHelp();
                        break;
                    case "--ignoreerrors":
                        ignoreErrors = true;
                        break;
                    case "--list":
                        list = true;
                        break;
                    case "--summary":
                        summary = true;
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    case "--version":
                        Console.WriteLine("LoadCS Version " + semVer);
                        break;
                    default:
                        Console.WriteLine("ERROR: Unknown option... " + strs[0]);
                        PrintHelp();
                        break;
                }
            }
            if (dump == "")
            {
                Console.WriteLine("ERROR: Must specify DUMP file name with --dumpfile=<dumpfile> option");
                System.Environment.Exit(1);
            }

            try
            {
                var dumpStream = new FileStream(dump, FileMode.Open, FileAccess.Read);
                using (var dumpReader = new BinaryReader(dumpStream, System.Text.Encoding.ASCII))
                {
                    dumpFile = new AosvsDumpFile(dumpReader);
                    if (summary || verbose) Console.WriteLine("Summary of DUMP file : {0}", dump);
                    dumpFile.Parse(extract, ignoreErrors, list, summary, verbose, "."); // or... Directory.GetCurrentDirectory());
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("ERROR: Could not open DUMP file {0} due to {1}", dump, e);
                System.Environment.Exit(1);
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: LoadCS [--help]|--dumpfile=<filename> [--version] [--extract] [--ignoreerrors] [--list] [--summary]");
            System.Environment.Exit(1);
        }
    }



    class AosvsDumpFile
    {
        public enum RecordType { StartDump, Fsb, NB, Uda, Acl, Link, StartBlock, DataBlock, EndBlock, EndDump }
        public enum FstatEntryType { Flnk = 0, Fdir = 12, Fdmp = 64, Fstf = 67, Ftxt = 68, Fprv = 74, Fprg = 87 }

        public const int DgDiskBlockBytes = 512;

        private BinaryReader dumpReader;
        private bool inFile = false;
        private bool loadIt = false;
        private char separator = Path.DirectorySeparatorChar;
        private string baseDir;
        private string workingDir;
        private FileStream writeStream;
        private DgDword totalFileSize = 0;

        public AosvsDumpFile(BinaryReader rdr)
        {
            this.dumpReader = rdr;
        }

        public byte[] ReadBlob(int len)
        {
            byte[] buffer = dumpReader.ReadBytes(len);
            if (buffer.Length != len)
            {
                Console.WriteLine("ERROR: Could not read Blob of length: {0} from DUMP file - aborting.", len);
                System.Environment.Exit(1);
            }
            return buffer;
        }

        public DgWord ReadWord()
        {
            var twoBytes = ReadBlob(2);
            return (DgWord)(((DgWord)twoBytes[0] << 8) | (DgWord)twoBytes[1]);
        }

        internal void ProcessDataBlock(RecordHeader recHdr, bool verbose, bool extract)
        {
            var baBytes = ReadBlob(4);
            DgDword ba = (DgDword)baBytes[0] << 24;
            ba |= (DgDword)baBytes[1] << 16;
            ba |= (DgDword)baBytes[2] << 8;
            ba |= (DgDword)baBytes[3];

            var blBytes = ReadBlob(4);
            DgDword bl = (DgDword)blBytes[0] << 24;
            bl |= (DgDword)blBytes[1] << 16;
            bl |= (DgDword)blBytes[2] << 8;
            bl |= (DgDword)blBytes[3];

            var aln = ReadWord();

            if (verbose) Console.WriteLine(" Data Block: {0} (bytes)", bl);

            // skip any alignment bytes (usually zero or one, could be more in theory)
            if (aln > 0)
            {
                if (verbose) Console.WriteLine("  Skipping {0} alignment byte(s)", aln);
                _ = ReadBlob(aln);
            }

            var dataBlob = ReadBlob((int)bl);

            if (extract)
            {
                try
                {
                    // large areas of NULLs may be skipped over by DUMP_II/III
                    // this is achieved by simply advancing the block address so
                    // we must pad out if block address is beyond end of last block
                    if (ba > totalFileSize + 1)
                    {
                        var paddingSize = ba - totalFileSize;
                        var paddingBlocks = paddingSize / DgDiskBlockBytes;
                        var paddingBlock = new byte[DgDiskBlockBytes];
                        for (int p = 0; p < paddingBlocks; p++)
                        {
                            if (verbose) Console.WriteLine("  Padding with one block");
                            writeStream.Write(paddingBlock);
                            totalFileSize += DgDiskBlockBytes;
                        }
                    }
                    writeStream.Write(dataBlob);
                }
                catch (Exception e)
                {
                    Console.WriteLine("ERROR: Could not write data to file - {0}", e.Message);
                    System.Environment.Exit(1);
                }
            }
            totalFileSize += bl;
            inFile = true;
        }

        internal void ProcessEndBlock(bool verbose, bool extract, bool summary)
        {
            if (inFile)
            {   // End of a file
                if (extract && loadIt) writeStream.Close();
                if (summary) Console.WriteLine(" {0, 12} bytes", totalFileSize);
                totalFileSize = 0;
                inFile = false;
            }
            else
            {   // "End of a Directory" i.e. move up to superior one
                // don't move above start dir for safety...
                if (workingDir != baseDir) workingDir = Directory.GetParent(workingDir).ToString();
                if (verbose) Console.WriteLine(" Popped dir - new dir is: " + workingDir);
            }

            if (verbose) Console.WriteLine("End Block Processed");
        }

        internal void ProcessLink(RecordHeader recHdr, string linkName,bool verbose, bool extract, bool summary)
        {
            var link = linkName;
            var linkTargetBytes = ReadBlob(recHdr.recordLength);
            // convert AOS/VS : directory separators to platform-specific ones and ensure upper case
            var linkTarget = System.Text.Encoding.ASCII.GetString(linkTargetBytes).Replace(':', separator).ToUpper();
            if (summary || verbose) Console.WriteLine(" -> Link Target: " + linkTarget);
            // Oh dear - I can't face trying to do this in C#...
            //if (extract)
            //{
            //    string targetName;
            //    if (workingDir == "") targetName = linkTarget; else
            //    {
            //        targetName = workingDir + separator + linkTarget;
            //        link = workingDir + separator + linkName;
            //    } 
            //}
        }

        internal string ProcessNameBlock(RecordHeader recHdr, byte[] fsbBlob, bool summary, bool verbose, bool extract, bool ignoreErrors)
        {
            string fileType;
            var nameBytes = ReadBlob(recHdr.recordLength);
            var fileName = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            if (summary && verbose) Console.WriteLine("");
            var entryType = (FstatEntryType)fsbBlob[1];
            switch (entryType)
            {
                case FstatEntryType.Flnk:
                    fileType = "=>Link=>";
                    loadIt = false;
                    break;
                case FstatEntryType.Fdir:
                    fileType = "<Directory>";
                    workingDir += separator + fileName;
                    if (extract)
                    {
                        try
                        {
                            Directory.CreateDirectory(workingDir);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("ERROR: Could not create directory <{0}> due to {1}", workingDir, e.Message);
                            if (!ignoreErrors)
                            {
                                Console.WriteLine("Giving up."); System.Environment.Exit(1);
                            }
                        }
                    }
                    loadIt = false;
                    break;
                case FstatEntryType.Fstf:
                    fileType = "Symbol Table";
                    loadIt = true;
                    break;
                case FstatEntryType.Ftxt:
                    fileType = "Text File";
                    loadIt = true;
                    break;
                case FstatEntryType.Fprg:
                case FstatEntryType.Fprv:
                    fileType = "Program File";
                    loadIt = true;
                    break;
                default:
                    // we don't explicitly recognise the type
                    // TODO Get a definitive list from paru.32.sr
                    fileType = "File";
                    loadIt = true;
                    break;
            }
            if (summary)
            {
                var displayPath = workingDir == "" ? fileName : workingDir + separator + fileName;
                Console.Write("{0, -12}: {1}", fileType, displayPath);
                if (verbose || entryType == FstatEntryType.Fdir) Console.WriteLine(""); else Console.Write("\t");
            }
            if (extract && loadIt)
            {
                var writePath = workingDir == "" ? fileName : workingDir + separator + fileName;
                if (verbose) Console.WriteLine(" Creating file: " + writePath);
                try
                {
                    writeStream = File.Create(writePath);
                }
                catch (Exception e)
                {
                    Console.WriteLine("ERROR: Could not create file {0} due to {1}", writePath, e.Message);
                    if (!ignoreErrors)
                    {
                        Console.WriteLine("Giving up.");
                        System.Environment.Exit(1);
                    }
                }
            }

            return fileName;
        }

        public void Parse(bool extract, bool ignoreErrors, bool list, bool summary, bool verbose, string baseDir)
        {
            byte[] fsbBlob = new byte[0];
            string fileName = "";
            this.baseDir = baseDir; // we won't ever traverse above this
            workingDir = baseDir;

            // There should always be a SOD record...
            Sod sod = ReadSod();
            if (summary || verbose)
            {
                Console.WriteLine("AOS/VS DUMP version  : {0}", sod.DumpFormatRevision);
                Console.WriteLine("DUMP date (y-m-d)    : {0}-{1}-{2}", sod.DumpTimeYear, sod.DumpTimeMonth, sod.DumpTimeDay);
                Console.WriteLine("DUMP time (h:m:s)    : {0}-{1}-{2}", sod.DumpTimeHours, sod.DumpTimeMins, sod.DumpTimeSecs);
            }
            // Now work through the dump examining each block type and handle accoordingly...
            bool done = false;
            while (!done)
            {
                var recHdr = ReadHeader();
                if (verbose) Console.WriteLine("Found block of type: {0} length: {1}", recHdr.recordType, recHdr.recordLength);
                switch (recHdr.recordType)
                {
                    case RecordType.StartDump:
                        Console.WriteLine("ERROR: Another START record found in DUMP - this should not happen.");
                        System.Environment.Exit(1);
                        break;
                    case RecordType.Fsb:
                        fsbBlob = ReadBlob(recHdr.recordLength);
                        loadIt = false;
                        break;
                    case RecordType.NB:
                        fileName = ProcessNameBlock(recHdr, fsbBlob, summary, verbose, extract, ignoreErrors);
                        break;
                    case RecordType.Uda:
                        // throw away for now
                        _ = ReadBlob(recHdr.recordLength);
                        break;
                    case RecordType.Acl:
                        var aclBlob = ReadBlob(recHdr.recordLength);
                        if (verbose) Console.WriteLine(" ACL: {0}", System.Text.Encoding.ASCII.GetString(aclBlob).TrimEnd('\0'));
                        break;
                    case RecordType.Link:
                        ProcessLink(recHdr, fileName, verbose, extract, summary);
                        break;
                    case RecordType.StartBlock:
                        // nothing to do - it's a standalone recHdr
                        break;
                    case RecordType.DataBlock:
                        ProcessDataBlock(recHdr, verbose, extract);
                        break;
                    case RecordType.EndBlock:
                        ProcessEndBlock(verbose, extract, summary);
                        break;
                    case RecordType.EndDump:
                        Console.WriteLine("=== End of DUMP ===");
                        done = true;
                        break;
                }
            }

        }

        public struct RecordHeader
        {
            public RecordType recordType;
            public int recordLength;
        }

        public RecordHeader ReadHeader()
        {
            var twoBytes = ReadBlob(2);
            RecordHeader hdr = new RecordHeader();
            int rtInt = (twoBytes[0] >> 2) & 0xff;
            hdr.recordType = (RecordType)rtInt;
            hdr.recordLength = (int)(((twoBytes[0] & 0x03) << 8) | twoBytes[1]);
            return hdr;
        }

        public struct Sod   // A close approximation of the internal Start-Of-DUMP record
        {
            public RecordHeader Header;
            public DgWord DumpFormatRevision;
            public DgWord DumpTimeSecs;
            public DgWord DumpTimeMins;
            public DgWord DumpTimeHours;
            public DgWord DumpTimeDay;
            public DgWord DumpTimeMonth;
            public DgWord DumpTimeYear;
        }
        public Sod ReadSod()
        {
            Sod sod = new Sod();
            sod.Header = ReadHeader();
            if (sod.Header.recordType != RecordType.StartDump)
            {
                Console.WriteLine("ERROR: This does not appear to be an AOS/VS DUMP_II or DUMP_III file (No SOD record found).");
                System.Environment.Exit(1);
            }
            sod.DumpFormatRevision = ReadWord();
            sod.DumpTimeSecs = ReadWord();
            sod.DumpTimeMins = ReadWord();
            sod.DumpTimeHours = ReadWord();
            sod.DumpTimeDay = ReadWord();
            sod.DumpTimeMonth = ReadWord();
            sod.DumpTimeYear = ReadWord();
            return sod;
        }
    }
}
