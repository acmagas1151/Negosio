namespace Negosio.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<NegosioApiFactory>
{
    public const string Name = "api";
}
