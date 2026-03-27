using Microsoft.Extensions.Options;

namespace LeaderElection.Postgres;

[OptionsValidator]
public partial class PostgresSettingsValidator : IValidateOptions<PostgresSettings> { }
