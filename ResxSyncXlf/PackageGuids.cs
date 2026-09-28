using System;

namespace ResxEditor
{
    internal static class PackageGuids
    {
        public const string ResxSyncPackageString = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        public static readonly Guid ResxSyncPackage = new Guid(ResxSyncPackageString);

        public const string LanguageManagerWindowString = "b2c3d4e5-f6a7-8901-cdef-012345678901";
        public static readonly Guid LanguageManagerWindow = new Guid(LanguageManagerWindowString);

        // Command set GUID for the Tools menu command that opens the Language Manager.
        public const string LanguageManagerCmdSetString = "c3d4e5f6-a7b8-9012-def0-123456789012";
        public static readonly Guid LanguageManagerCmdSet = new Guid(LanguageManagerCmdSetString);
        public const int LanguageManagerCommandId = 0x0100;
    }
}
