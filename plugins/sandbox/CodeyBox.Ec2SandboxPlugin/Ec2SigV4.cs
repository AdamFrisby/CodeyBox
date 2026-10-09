using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>
/// Pure AWS Signature Version 4 signer for EC2 Query requests. No SDK, no
/// ambient credential chain: the caller hands the key pair explicitly and the
/// signer returns header values only. Kept pure (inputs in, signature out) so
/// the exact bytes are unit-testable against the published AWS test vectors
/// without any network.
///
/// <para>Only the unsigned-payload form is <em>not</em> used: the SHA-256 of
/// the exact form body is always signed, so a tampered body fails closed.</para>
/// </summary>
public static class Ec2SigV4Signer
{
    /// <summary>Service name signed for every EC2 request.</summary>
    public const string Service = "ec2";

    /// <summary>Terminating string in the credential scope.</summary>
    public const string Terminator = "aws4_request";

    /// <summary>Algorithm name in the Authorization header.</summary>
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Signs one EC2 POST body. Returns the <c>Authorization</c> header value
    /// and the <c>x-amz-date</c> timestamp; the caller attaches
    /// <c>x-amz-security-token</c> itself when a session token is present.
    /// </summary>
    public static (string Authorization, string AmzDate) Sign(
        string accessKeyId,
        string secretAccessKey,
        string region,
        string host,
        string payload,
        DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(payload);

        var utc = timestamp.UtcDateTime;
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var dateStamp = utc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var payloadHash = Sha256Hex(payload);
        var canonicalRequest = CanonicalRequest(host, payloadHash, amzDate);
        var credentialScope = $"{dateStamp}/{region}/{Service}/{Terminator}";
        var stringToSign = $"{Algorithm}\n{amzDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";
        var signingKey = DeriveSigningKey(secretAccessKey, dateStamp, region);
        var signature = HmacHex(signingKey, stringToSign);
        var authorization =
            $"{Algorithm} Credential={accessKeyId}/{credentialScope}, " +
            $"SignedHeaders=host;x-amz-date, Signature={signature}";
        return (authorization, amzDate);
    }

    internal static string CanonicalRequest(string host, string payloadHash, string amzDate)
    {
        var builder = new StringBuilder();
        builder.Append("POST\n");
        builder.Append("/\n");
        builder.Append('\n');
        builder.Append("host:").Append(host.Trim().ToLowerInvariant()).Append('\n');
        builder.Append("x-amz-date:").Append(amzDate).Append('\n');
        builder.Append('\n');
        builder.Append("host;x-amz-date\n");
        builder.Append(payloadHash);
        return builder.ToString();
    }

    internal static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var key = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        key = Hmac(key, dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, Service);
        key = Hmac(key, Terminator);
        return key;
    }

    internal static string Sha256Hex(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    internal static string HmacHex(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }
}

/// <summary>
/// Pure validators for EC2 placement pins (region/zone/AMI/instance
/// type/subnet/VPC/security-group/volume type). No network: syntactic pins
/// the operator must set exactly, so a typo fails closed at validation time
/// instead of provisioning somewhere unintended.
/// </summary>
public static class Ec2Placement
{
    /// <summary>true when the value looks like an AWS region (e.g. us-east-1, eu-central-1).</summary>
    public static bool IsValidRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var region = value.Trim();
        if (region.Length is < 9 or > 25)
            return false;
        var dash = region.LastIndexOf('-');
        if (dash <= 0 || dash + 1 >= region.Length)
            return false;
        if (!int.TryParse(
                region[(dash + 1)..],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index) || index < 1 || index > 9)
        {
            return false;
        }
        foreach (var ch in region[..dash])
        {
            if ((ch < 'a' || ch > 'z') && ch != '-')
                return false;
        }
        return true;
    }

    /// <summary>true when the zone belongs to the region (region name plus one trailing letter, or fips-local).</summary>
    public static bool IsValidZone(string? zone, string region)
    {
        if (string.IsNullOrWhiteSpace(zone) || string.IsNullOrWhiteSpace(region))
            return false;
        var name = zone.Trim();
        if (name.StartsWith(region.Trim(), StringComparison.Ordinal)
            && name.Length == region.Trim().Length + 1)
        {
            var suffix = name[^1];
            return suffix >= 'a' && suffix <= 'z';
        }
        return false;
    }

    /// <summary>true for concrete AMI ids (<c>ami-</c> plus 8–17 lowercase hex chars).</summary>
    public static bool IsValidAmiId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var id = value.Trim();
        if (!id.StartsWith("ami-", StringComparison.Ordinal) || id.Length is < 12 or > 25)
            return false;
        foreach (var ch in id["ami-".Length..])
        {
            if ((ch < '0' || ch > '9') && (ch < 'a' || ch > 'f'))
                return false;
        }
        return true;
    }

    /// <summary>true for instance-type shape (<c>family.size</c>, lowercase alphanumerics and dots).</summary>
    public static bool IsValidInstanceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var type = value.Trim();
        if (type.Length is < 5 or > 32)
            return false;
        var dot = type.IndexOf('.');
        if (dot <= 0 || dot == type.Length - 1 || type.IndexOf('.', dot + 1) >= 0)
            return false;
        foreach (var ch in type)
        {
            if ((ch < 'a' || ch > 'z') && (ch < '0' || ch > '9') && ch != '.' && ch != '-')
                return false;
        }
        return true;
    }

    /// <summary>true for <c>subnet-</c> ids (hex suffix).</summary>
    public static bool IsValidSubnetId(string? value) => IsPrefixedHexId(value, "subnet-");

    /// <summary>true for <c>vpc-</c> ids (hex suffix).</summary>
    public static bool IsValidVpcId(string? value) => IsPrefixedHexId(value, "vpc-");

    /// <summary>true for <c>sg-</c> ids (hex suffix).</summary>
    public static bool IsValidSecurityGroupId(string? value) => IsPrefixedHexId(value, "sg-");

    /// <summary>true for <c>i-</c> instance ids (hex suffix).</summary>
    public static bool IsValidInstanceId(string? value) => IsPrefixedHexId(value, "i-");

    /// <summary>true for <c>vol-</c> volume ids (hex suffix).</summary>
    public static bool IsValidVolumeId(string? value) => IsPrefixedHexId(value, "vol-");

    /// <summary>true for <c>eipalloc-</c> allocation ids (hex suffix).</summary>
    public static bool IsValidAllocationId(string? value) => IsPrefixedHexId(value, "eipalloc-");

    /// <summary>true for <c>eipassoc-</c> association ids (hex suffix).</summary>
    public static bool IsValidAssociationId(string? value) => IsPrefixedHexId(value, "eipassoc-");

    private static bool IsPrefixedHexId(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var id = value.Trim();
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length <= prefix.Length + 8)
            return false;
        foreach (var ch in id[prefix.Length..])
        {
            if ((ch < '0' || ch > '9') && (ch < 'a' || ch > 'f'))
                return false;
        }
        return true;
    }

    /// <summary>EBS volume types the provider sends verbatim.</summary>
    public static readonly IReadOnlySet<string> AllowedVolumeTypes =
        new HashSet<string>(StringComparer.Ordinal) { "gp2", "gp3", "io1", "io2", "st1", "sc1", "standard" };
}
