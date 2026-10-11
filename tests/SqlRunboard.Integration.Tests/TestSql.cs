using Microsoft.Data.SqlClient;
using SqlRunboard.Core;

/// <summary>The test server: a SQL Server logon in the SQLRUNBOARD_TEST_SQL connection string. No variable: the tests fail, they do not skip.</summary>
static class TestSql
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("SQLRUNBOARD_TEST_SQL") is { Length: > 0 } s ? s
        : throw new InvalidOperationException("SQLRUNBOARD_TEST_SQL is not set. Set it to a SQL Server connection string with a SQL Server logon, " +
            "for example \"Server=localhost,1433;User ID=sa;Password=<password>;TrustServerCertificate=true\". compose.yaml starts a local server.");

    /// <summary>The connection string as a batch target and a password, as the page gives them.</summary>
    public static SqlConnectionSettings Settings()
    {
        var b = new SqlConnectionStringBuilder(ConnectionString);
        return new SqlConnectionSettings(new BatchTarget
        {
            Server = b.DataSource,
            Database = b.InitialCatalog is "" ? "master" : b.InitialCatalog,
            Auth = Auth.SqlServer,
            User = b.UserID,
            Encrypt = b.Encrypt == SqlConnectionEncryptOption.Strict ? Encrypt.Strict : b.Encrypt == SqlConnectionEncryptOption.Optional ? Encrypt.Optional : Encrypt.Mandatory,
            TrustServerCertificate = b.TrustServerCertificate,
        }, b.Password);
    }
}
