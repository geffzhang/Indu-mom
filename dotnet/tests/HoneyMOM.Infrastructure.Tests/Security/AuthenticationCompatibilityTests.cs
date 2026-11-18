using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HoneyMOM.Application.Configuration;
using HoneyMOM.Infrastructure.Security;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Security;

public sealed class AuthenticationCompatibilityTests
{
    [Fact]
    public void AcceptsPythonHashAndTokenFixtures()
    {
        const string password = "密码🔑";
        const string pythonHash =
            "pbkdf2$0123456789abcdef$e7b4f7c107b7c7358e1ee8da9a38c2cc7a6b2733a21e1feb74fbc362effb1f39";
        const string pythonToken =
            "eyJ1IjogInB5dGhvbi1maXh0dXJlIiwgImV4cCI6IDE3MDA2MDQ4MDB9.6aaa3d44e14408f1acc7cfbc2e71aa2b";
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        using var temporaryDirectory = new TemporaryDirectory();
        var dataDirectory = Path.Combine(temporaryDirectory.Path, "existing-secret");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "secret.key"), secret);

        var hasher = new PythonCompatiblePasswordHasher();
        Assert.True(hasher.Verify(password, pythonHash));
        Assert.False(hasher.Verify(password, "pbkdf2$bad"));
        Assert.False(hasher.Verify(password, "pbkdf2$0123456789abcdef$not-hex"));

        var generatedHash = hasher.Hash(password);
        Assert.Matches(
            new Regex(@"^pbkdf2\$[0-9a-f]{16}\$[0-9a-f]{64}$", RegexOptions.CultureInvariant),
            generatedHash);
        Assert.True(hasher.Verify(password, generatedHash));

        var options = new MomOptions(dataDirectory, "", 30, 10);
        var tokenService = new PythonCompatibleTokenService(options);
        Assert.Equal(pythonToken, tokenService.Create("python-fixture", now));
        Assert.Equal("python-fixture", tokenService.ReadUsername(pythonToken, now));
        Assert.Null(tokenService.ReadUsername(pythonToken, DateTimeOffset.FromUnixTimeSeconds(1_700_604_800)));
        Assert.Null(tokenService.ReadUsername(pythonToken[..^1] + "0", now));
        Assert.Null(tokenService.ReadUsername(CreateSignedToken("not-json", secret), now));
        Assert.Equal(secret, File.ReadAllText(Path.Combine(dataDirectory, "secret.key")));

        const string generatedUsername = "测试-user";
        var generatedToken = tokenService.Create(generatedUsername, now);
        Assert.Equal(generatedUsername, tokenService.ReadUsername(generatedToken, now));
        VerifyWithPython(
            FindRepositoryRoot(),
            password,
            generatedHash,
            generatedToken,
            secret,
            now,
            generatedUsername);

        var generatedSecretDirectory = Path.Combine(temporaryDirectory.Path, "generated-secret");
        var generatedTokenService = new PythonCompatibleTokenService(
            options with { DataDirectory = generatedSecretDirectory });
        generatedTokenService.Create("generated", now);
        var generatedSecret = File.ReadAllText(Path.Combine(generatedSecretDirectory, "secret.key"));
        Assert.Matches(new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant), generatedSecret);
        new PythonCompatibleTokenService(options with { DataDirectory = generatedSecretDirectory })
            .Create("generated", now);
        Assert.Equal(
            generatedSecret,
            File.ReadAllText(Path.Combine(generatedSecretDirectory, "secret.key")));
    }

    private static void VerifyWithPython(
        string repositoryRoot,
        string password,
        string generatedHash,
        string generatedToken,
        string secret,
        DateTimeOffset now,
        string username)
    {
        var nestedReferencePath = Path.Combine(repositoryRoot, "honeymom-python-reference");
        var referencePath = Directory.Exists(nestedReferencePath)
            ? nestedReferencePath
            : Path.GetFullPath(Path.Combine(repositoryRoot, "..", "honeymom-python-reference"));
        var startInfo = new ProcessStartInfo("python")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "import sys; from honeymom import auth; " +
            "auth._secret = sys.argv[1].encode(); auth.time.time = lambda: int(sys.argv[2]); " +
            "assert auth.check_pw(sys.argv[3], sys.argv[4]); " +
            "assert auth.read_token(sys.argv[5]) == sys.argv[6]");
        startInfo.ArgumentList.Add(secret);
        startInfo.ArgumentList.Add(now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(password);
        startInfo.ArgumentList.Add(generatedHash);
        startInfo.ArgumentList.Add(generatedToken);
        startInfo.ArgumentList.Add(username);
        var existingPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        startInfo.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existingPythonPath)
            ? referencePath
            : referencePath + Path.PathSeparator + existingPythonPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Python authentication interoperability check.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Python rejected .NET authentication values ({process.ExitCode}).{Environment.NewLine}{stdout}{stderr}");
    }

    private static string CreateSignedToken(string payload, string secret)
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .Replace('+', '-')
            .Replace('/', '_');
        var signature = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant()[..32];
        return $"{body}.{signature}";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "porting", "reference-lock.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository porting reference lock.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"honeymom-auth-test-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
