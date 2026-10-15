using PCBoost.Infrastructure.Updates;

namespace PCBoost.Core.Tests.CrossCutting;

/// <summary>
/// Manifestes signés par les clés de PRODUCTION (avec l'outil de publication Python, sur le poste de l'éditeur) : vérifient
/// que la signature produite par la publication est acceptée par PCBoost, et que les clés intégrées sont les bonnes.
/// Version 0.0.1 : jamais plus récente qu'une version installée, donc sans danger dans un dépôt public.
/// </summary>
public sealed class ProductionManifestTests
{
    private const string SignedByPrimaryKey = """
        {"format":1,"payload":"eyJwcm9kdWN0IjoiUENCb29zdCIsInZlcnNpb24iOiIwLjAuMSIsInB1Ymxpc2hlZEF0IjoiMjAyNi0xMC0xNVQwMDowMDowMFoiLCJub3RlcyI6Ik1hbmlmZXN0ZSBkZSBjb250csO0bGUgZGVzIHRlc3RzICh2ZXJzaW9uIDAuMC4xLCBqYW1haXMgaW5zdGFsbGFibGUpLiIsInJlbGVhc2VVcmwiOiJodHRwczovL2dpdGh1Yi5jb20vTkVURklYMjUzL1BDQm9vc3QvcmVsZWFzZXMvdGFnL3YwLjAuMSIsInBhY2thZ2VzIjpbeyJhcmNoIjoieDY0Iiwia2luZCI6Im1zaSIsImZpbGVOYW1lIjoiUENCb29zdC0wLjAuMS14NjQubXNpIiwidXJsIjoiaHR0cHM6Ly9naXRodWIuY29tL05FVEZJWDI1My9QQ0Jvb3N0L3JlbGVhc2VzL2Rvd25sb2FkL3YwLjAuMS9QQ0Jvb3N0LTAuMC4xLXg2NC5tc2kiLCJzaXplIjoxMDQ4NTc2LCJzaGEyNTYiOiIwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwIn0seyJhcmNoIjoiYXJtNjQiLCJraW5kIjoibXNpIiwiZmlsZU5hbWUiOiJQQ0Jvb3N0LTAuMC4xLWFybTY0Lm1zaSIsInVybCI6Imh0dHBzOi8vZ2l0aHViLmNvbS9ORVRGSVgyNTMvUENCb29zdC9yZWxlYXNlcy9kb3dubG9hZC92MC4wLjEvUENCb29zdC0wLjAuMS1hcm02NC5tc2kiLCJzaXplIjoxMDQ4NTc2LCJzaGEyNTYiOiIwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwIn1dfQ==","signatures":[{"keyId":"pcboost-update-2026-1","sig":"MEUCIFAz9R9LtPwnD3Z6r1Fx0oPKoG5PkbxqldaAFTM1n2F6AiEAxsnj/rhfpe+om3Bv1PZCraOyMMsT/CIg7NIYRWU50+Y="}]}
        """;

    private const string SignedByRecoveryKey = """
        {"format":1,"payload":"eyJwcm9kdWN0IjoiUENCb29zdCIsInZlcnNpb24iOiIwLjAuMSIsInB1Ymxpc2hlZEF0IjoiMjAyNi0xMC0xNVQwMDowMDowMFoiLCJub3RlcyI6Ik1hbmlmZXN0ZSBkZSBjb250csO0bGUgZGVzIHRlc3RzICh2ZXJzaW9uIDAuMC4xLCBqYW1haXMgaW5zdGFsbGFibGUpLiIsInJlbGVhc2VVcmwiOiJodHRwczovL2dpdGh1Yi5jb20vTkVURklYMjUzL1BDQm9vc3QvcmVsZWFzZXMvdGFnL3YwLjAuMSIsInBhY2thZ2VzIjpbeyJhcmNoIjoieDY0Iiwia2luZCI6Im1zaSIsImZpbGVOYW1lIjoiUENCb29zdC0wLjAuMS14NjQubXNpIiwidXJsIjoiaHR0cHM6Ly9naXRodWIuY29tL05FVEZJWDI1My9QQ0Jvb3N0L3JlbGVhc2VzL2Rvd25sb2FkL3YwLjAuMS9QQ0Jvb3N0LTAuMC4xLXg2NC5tc2kiLCJzaXplIjoxMDQ4NTc2LCJzaGEyNTYiOiIwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwIn0seyJhcmNoIjoiYXJtNjQiLCJraW5kIjoibXNpIiwiZmlsZU5hbWUiOiJQQ0Jvb3N0LTAuMC4xLWFybTY0Lm1zaSIsInVybCI6Imh0dHBzOi8vZ2l0aHViLmNvbS9ORVRGSVgyNTMvUENCb29zdC9yZWxlYXNlcy9kb3dubG9hZC92MC4wLjEvUENCb29zdC0wLjAuMS1hcm02NC5tc2kiLCJzaXplIjoxMDQ4NTc2LCJzaGEyNTYiOiIwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwIn1dfQ==","signatures":[{"keyId":"pcboost-update-recovery","sig":"MEUCIB/BkKy/aJKKEeHIBq1pnLVtIJEBmk7QBHfU72yX2vA9AiEAvSTnXCjL5Qq3DRRgD6GDgoo34kehAL3j8IQ+F/OM91s="}]}
        """;

    [Theory]
    [InlineData(SignedByPrimaryKey, UpdateTrust.PrimaryKeyId)]
    [InlineData(SignedByRecoveryKey, UpdateTrust.RecoveryKeyId)]
    public void Manifests_signed_by_the_publishing_tool_are_accepted_with_the_embedded_keys(string manifest, string keyId)
    {
        var result = SignedUpdateManifest.Verify(manifest, UpdateTrust.ProductionKeys, UpdateSourceRules.ForGitHub("NETFIX253/PCBoost"));

        Assert.True(result.Success, result.TechnicalDetail);
        Assert.Equal(keyId, result.Value!.KeyId);
        Assert.Equal(new Version(0, 0, 1), result.Value.Version);
        Assert.Equal(2, result.Value.Packages.Count);
        Assert.NotNull(result.Value.PackageFor("x64"));
    }

    [Fact]
    public void A_production_manifest_is_refused_with_other_keys_or_another_repository()
    {
        var otherKeys = new Dictionary<string, string> { [UpdateTrust.PrimaryKeyId] = UpdateTrust.ProductionKeys[UpdateTrust.RecoveryKeyId] };

        Assert.Equal("Infra_Update_SignatureInvalid",
            SignedUpdateManifest.Verify(SignedByPrimaryKey, otherKeys, UpdateSourceRules.ForGitHub("NETFIX253/PCBoost")).Message!.Key);
        Assert.Equal("Infra_Update_FeedInvalid",
            SignedUpdateManifest.Verify(SignedByPrimaryKey, UpdateTrust.ProductionKeys, UpdateSourceRules.ForGitHub("autre/depot")).Message!.Key);
    }

    [Fact]
    public void Embedded_keys_are_distinct_p256_public_keys()
    {
        Assert.Equal(2, UpdateTrust.ProductionKeys.Values.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in UpdateTrust.ProductionKeys.Values)
        {
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }
}
