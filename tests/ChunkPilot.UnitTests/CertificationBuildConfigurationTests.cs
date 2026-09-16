using ChunkPilot.Certification;

namespace ChunkPilot.UnitTests;

public sealed class CertificationBuildConfigurationTests
{
    private const string Service = "https://service.example/";
    private const string DisabledHash = "e8f99faf6874d8facb1554162184d6eb25a5c9a5e73ad15c30d336bedd580f1e";
    private const string ServiceHash = "01cfbe1c630acc8d405fc11ac0be4d74c83da263187315ea829271d84594b924";

    [Theory]
    [InlineData("", DisabledHash)]
    [InlineData(Service, ServiceHash)]
    public void Configuration_hash_matches_the_PowerShell_UTF8_vectors(string endpoint, string expected)
    {
        Assert.Equal(expected, CertificationPackageFreshness.ComputeBuildConfigurationSha256(endpoint));
        CertificationPackageFreshness.ValidateBuildConfiguration(endpoint, expected, endpoint);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("https://service.example", Service)]
    [InlineData(Service, Service)]
    [InlineData("https://service.example:443", Service)]
    [InlineData("https://service.example:443/", Service)]
    public void Normalization_preserves_disabled_mode_and_equivalent_public_origins(string? input, string expected) =>
        Assert.Equal(expected, CertificationPackageFreshness.NormalizeServiceEndpoint(input));

    [Theory]
    [InlineData("https://service.example")]
    [InlineData(Service)]
    [InlineData("https://service.example:443/")]
    public void Canonical_manifest_accepts_equivalent_compiled_endpoint(string compiledEndpoint) =>
        CertificationPackageFreshness.ValidateBuildConfiguration(Service, ServiceHash, compiledEndpoint);

    [Theory]
    [InlineData("", Service)]
    [InlineData(Service, "")]
    [InlineData(Service, "https://other.example/")]
    [InlineData("https://other.example/", Service)]
    public void Matching_source_cannot_substitute_a_different_build_configuration(string manifestEndpoint, string compiledEndpoint) =>
        Assert.Throws<InvalidDataException>(() => CertificationPackageFreshness.ValidateBuildConfiguration(
            manifestEndpoint, CertificationPackageFreshness.ComputeBuildConfigurationSha256(manifestEndpoint), compiledEndpoint));

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, DisabledHash)]
    [InlineData("", null)]
    [InlineData(Service, null)]
    public void Manifest_requires_both_explicit_configuration_fields(string? endpoint, string? hash) =>
        Assert.Throws<InvalidDataException>(() =>
            CertificationPackageFreshness.ValidateBuildConfiguration(endpoint, hash, endpoint ?? ""));

    [Theory]
    [InlineData("")]
    [InlineData("bad-hash")]
    [InlineData("01cfbe1c630acc8d405fc11ac0be4d74c83da263187315ea829271d84594b92")]
    [InlineData("01cfbe1c630acc8d405fc11ac0be4d74c83da263187315ea829271d84594b9240")]
    [InlineData("g1cfbe1c630acc8d405fc11ac0be4d74c83da263187315ea829271d84594b924")]
    [InlineData("01CFBE1C630ACC8D405FC11AC0BE4D74C83DA263187315EA829271D84594B924")]
    [InlineData(DisabledHash)]
    public void Manifest_hash_must_be_the_exact_canonical_configuration_digest(string hash) =>
        Assert.Throws<InvalidDataException>(() =>
            CertificationPackageFreshness.ValidateBuildConfiguration(Service, hash, Service));

    [Theory]
    [InlineData("https://service.example")]
    [InlineData("https://service.example:443/")]
    public void Manifest_endpoint_must_already_be_canonical_even_with_its_own_matching_hash(string endpoint) =>
        Assert.Throws<InvalidDataException>(() => CertificationPackageFreshness.ValidateBuildConfiguration(
            endpoint, CertificationPackageFreshness.ComputeBuildConfigurationSha256(endpoint), Service));

    [Theory]
    [InlineData("http://service.example/")]
    [InlineData("HTTPS://service.example/")]
    [InlineData("https://Service.example/")]
    [InlineData("https://localhost/")]
    [InlineData("https://service.localhost/")]
    [InlineData("https://service.local/")]
    [InlineData("https://service/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://192.168.1.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://service.example./")]
    [InlineData("https://user:fixture@service.example/")]
    [InlineData("https://service.example/?key=fixture")]
    [InlineData("https://service.example/#fixture")]
    [InlineData("https://service.example/path")]
    [InlineData("https://service.example/%2f")]
    [InlineData("https://service.example:8443/")]
    [InlineData("https://service.example;OtherProperty=fixture")]
    [InlineData("https://service.example,OtherProperty=fixture")]
    [InlineData("https://service.example\n")]
    [InlineData(" https://service.example/")]
    [InlineData(" ")]
    public void Unsafe_or_noncanonical_origin_forms_are_rejected_before_use(string endpoint)
    {
        Assert.Throws<InvalidDataException>(() => CertificationPackageFreshness.NormalizeServiceEndpoint(endpoint));
        Assert.Throws<InvalidDataException>(() => CertificationPackageFreshness.ValidateBuildConfiguration(
            endpoint, CertificationPackageFreshness.ComputeBuildConfigurationSha256(endpoint), Service));
        Assert.Throws<InvalidDataException>(() =>
            CertificationPackageFreshness.ValidateBuildConfiguration(Service, ServiceHash, endpoint));
    }
}
