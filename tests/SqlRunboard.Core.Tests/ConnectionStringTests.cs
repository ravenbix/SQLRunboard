using Microsoft.Data.SqlClient;
using SqlRunboard.Core;

public class ConnectionStringTests
{
    static SqlConnectionStringBuilder Build(BatchTarget target, string? password = null, string? script = "001_tables.sql") =>
        new(SqlServerExecutor.ConnectionString(new SqlConnectionSettings(target, password), script));

    [Fact]
    public void Each_script_gets_its_own_unpooled_connection_named_after_it()
    {
        var b = Build(new BatchTarget { Server = "sql01", Database = "Sales" });
        Assert.Equal("sql01", b.DataSource);
        Assert.Equal("Sales", b.InitialCatalog);
        Assert.False(b.Pooling);
        Assert.Equal("SQLRunboard 001_tables.sql", b.ApplicationName);
    }

    [Fact]
    public void Test_connection_uses_the_bare_application_name_and_a_long_name_is_cut_to_128()
    {
        Assert.Equal("SQLRunboard", Build(new BatchTarget { Server = "s" }, script: null).ApplicationName);
        Assert.Equal(128, Build(new BatchTarget { Server = "s" }, script: new string('x', 200)).ApplicationName.Length);
    }

    [Fact]
    public void Integrated_logon_uses_integrated_security_and_no_password()
    {
        var b = Build(new BatchTarget { Server = "s", Auth = Auth.Integrated }, password: "ignored");
        Assert.True(b.IntegratedSecurity);
        Assert.Equal("", b.Password);
    }

    [Fact]
    public void SQL_Server_logon_uses_the_user_and_the_password()
    {
        var b = Build(new BatchTarget { Server = "s", Auth = Auth.SqlServer, User = "deploy" }, password: "pw");
        Assert.False(b.IntegratedSecurity);
        Assert.Equal("deploy", b.UserID);
        Assert.Equal("pw", b.Password);
    }

    [Theory]
    [InlineData(Encrypt.Mandatory, "Mandatory")]
    [InlineData(Encrypt.Optional, "Optional")]
    [InlineData(Encrypt.Strict, "Strict")]
    public void Encrypt_and_trust_server_certificate_go_into_the_connection_string(Encrypt encrypt, string expected)
    {
        var b = Build(new BatchTarget { Server = "s", Encrypt = encrypt, TrustServerCertificate = true });
        Assert.Equal(SqlConnectionEncryptOption.Parse(expected), b.Encrypt);
        Assert.True(b.TrustServerCertificate);
    }

    [Fact]
    public void The_additional_text_adds_other_settings_and_cannot_turn_pooling_on()
    {
        var b = Build(new BatchTarget { Server = "s", ExtraConnectionString = "Connect Timeout=7;Pooling=true;MultiSubnetFailover=true" });
        Assert.Equal(7, b.ConnectTimeout);
        Assert.True(b.MultiSubnetFailover);
        Assert.False(b.Pooling);
    }

    [Fact]
    public void Entra_logon_is_not_supported_yet()
    {
        var e = Assert.Throws<NotSupportedException>(() => Build(new BatchTarget { Server = "s", Auth = Auth.EntraDefault }));
        Assert.Contains("Entra", e.Message);
    }
}
