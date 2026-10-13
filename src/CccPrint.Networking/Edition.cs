namespace CccPrint.Networking;

public static class Edition
{
#if COMPANY_EDITION
    public const string Suffix = "";
    public const string InstancePrefix = @"Local\CCCPrint.";
    public const string InstallerId = "{0B43C92F-4FB6-4B40-9261-625EE17A6E26}";
#else
    public const string Suffix = "公开版";
    public const string InstancePrefix = @"Local\CCCPrint.Public.";
    public const string InstallerId = "{5A42B3DB-95E2-4BC4-96A3-990B6DA39F13}";
#endif
    public const string DataDirectory = "CCC资料打印系统" + Suffix;
    public const string ClientName = "CCC资料打印端" + Suffix;
}
