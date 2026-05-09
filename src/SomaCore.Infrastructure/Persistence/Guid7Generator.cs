using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace SomaCore.Infrastructure.Persistence;

public sealed class Guid7Generator : ValueGenerator<Guid>
{
    public override bool GeneratesTemporaryValues => false;

    public override Guid Next(EntityEntry entry) => NewId();

    public static Guid NewId() => Guid.CreateVersion7();
}
