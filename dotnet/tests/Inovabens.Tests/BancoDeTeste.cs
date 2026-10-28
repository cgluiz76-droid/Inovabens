namespace Inovabens.Tests;

/// <summary>Os testes de integração compartilham o mesmo banco: rodam em sequência, nunca em paralelo.</summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class BancoDeTeste
{
    public const string Nome = "Banco de teste";
}
