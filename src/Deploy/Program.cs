using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pulumi;
using Pulumi.Aws.Ecr;
using Pulumi.Docker;

return await Deployment.RunAsync(async () =>
{
    string rootDirectory = Environment.GetEnvironmentVariable("Root");
    string configuration = Environment.GetEnvironmentVariable("Configuration");

    var ecr = new Repository("minimalapi", new RepositoryArgs());

    var credentials = GetCredentials.Invoke(new GetCredentialsInvokeArgs
    {
        RegistryId = ecr.RegistryId
    });

    var decodedCredentials = credentials.Apply(o => Encoding.UTF8.GetString(
        Convert.FromBase64String(o.AuthorizationToken)));

    var registry = new ImageRegistry
    {
        Username = decodedCredentials.Apply(c => c.Split(":").First()),
        Password = decodedCredentials.Apply(c => c.Split(":").Last()),
        Server = credentials.Apply(c => c.ProxyEndpoint)
    };

    var image = new Image("minimalapi", new ImageArgs
    {
        Build = new DockerBuild
        {
            Context = rootDirectory,
            Dockerfile = $"{rootDirectory}/minimalapi.dockerfile",
            Args = new Dictionary<string, string>
            {
                ["CONFIGURATION"] = configuration
            }
        },
        ImageName = Output.Format($"{ecr.RepositoryUrl}:v1.0.0"),
        LocalImageName = "minimalapi:v1.0.0",
        Registry = registry
    });

    return new Dictionary<string, object?>
    {
        ["ImageName"] = image.ImageName
    };
});