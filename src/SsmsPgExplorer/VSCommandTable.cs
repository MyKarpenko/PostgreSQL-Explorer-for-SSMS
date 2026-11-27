// Kept in sync with VSCommandTable.vsct by hand (the VsctGenerator custom tool only runs inside the VS IDE).
using System;

namespace SsmsPgExplorer
{
    internal sealed partial class PackageGuids
    {
        public const string guidSsmsPgExplorerPackageString = "3b8f2c61-7a4d-4e95-b0c2-5d1e8f6a9b37";
        public static Guid guidSsmsPgExplorerPackage = new Guid(guidSsmsPgExplorerPackageString);

        public const string guidSsmsPgExplorerCmdSetString = "a6d41e28-3c9b-47f0-8e15-2b7c9d4f6e83";
        public static Guid guidSsmsPgExplorerCmdSet = new Guid(guidSsmsPgExplorerCmdSetString);
    }

    internal sealed partial class PackageIds
    {
        public const int ToolsGroup = 0x1020;
        public const int cmdidShowRegisteredServers = 0x0100;
        public const int cmdidNewPgQuery = 0x0101;
        public const int cmdidAttachObjectExplorer = 0x0102;
    }
}
