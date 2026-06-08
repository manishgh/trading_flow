using TradingFlow.Etoro.Configuration;

namespace TradingFlow.Etoro.Authentication;

public sealed class EtoroCredentialsProvider
{
    private readonly EtoroOptions options;

    public EtoroCredentialsProvider(EtoroOptions options)
    {
        this.options = options;
    }

    public EtoroCredentials GetCredentials()
    {
        ValidateSecretSeparation();
        var profile = options.ActiveProfile;
        var apiKey = Environment.GetEnvironmentVariable(profile.ApiKeyEnv);
        var userKey = Environment.GetEnvironmentVariable(profile.UserKeyEnv);

        if (String.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Missing eToro API key environment variable: {profile.ApiKeyEnv}.");
        }

        if (String.IsNullOrWhiteSpace(userKey))
        {
            throw new InvalidOperationException($"Missing eToro user key environment variable: {profile.UserKeyEnv}.");
        }

        return new EtoroCredentials(apiKey, userKey);
    }

    private void ValidateSecretSeparation()
    {
        var inactiveProfile = options.Environment == EtoroEnvironment.Demo ? options.Live : options.Demo;
        var inactiveApiKey = Environment.GetEnvironmentVariable(inactiveProfile.ApiKeyEnv);
        var inactiveUserKey = Environment.GetEnvironmentVariable(inactiveProfile.UserKeyEnv);
        if (!String.IsNullOrWhiteSpace(inactiveApiKey) || !String.IsNullOrWhiteSpace(inactiveUserKey))
        {
            throw new InvalidOperationException(
                $"eToro {options.Environment} process also has inactive credential variables mounted. " +
                $"Remove {inactiveProfile.ApiKeyEnv} and {inactiveProfile.UserKeyEnv} from this app.");
        }
    }
}
