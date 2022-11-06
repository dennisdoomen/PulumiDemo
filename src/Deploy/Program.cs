using System;
using System.Collections.Generic;
using Pulumi;
using Pulumi.Aws.Ecs;
using Pulumi.Awsx.Ecr;
using Pulumi.Awsx.Ecs;
using Pulumi.Awsx.Ecs.Inputs;
using Pulumi.Awsx.Lb;

return await Deployment.RunAsync(() =>
{
    string? rootDirectory = Environment.GetEnvironmentVariable("Root");
    ArgumentNullException.ThrowIfNull(rootDirectory);

    string configuration = Environment.GetEnvironmentVariable("Configuration") ?? "Debug";

    var ecr = new Repository("minimal-ecr");

    var image = new Image("minimal-image", new ImageArgs
    {
        RepositoryUrl = ecr.Url,
        Dockerfile = $"{rootDirectory}/dockerfile",
        Path = $"{rootDirectory}",
        Args = new InputMap<string>
        {
            { "CONFIGURATION", configuration }
        },
    });

    var cluster = new Cluster("minimal-cluster");

    var lb = new ApplicationLoadBalancer("minimal-alb");

    var service = new FargateService("minimal-service", new FargateServiceArgs
    {
        Cluster = cluster.Arn,
        TaskDefinitionArgs = new FargateServiceTaskDefinitionArgs
        {
            Container = new TaskDefinitionContainerDefinitionArgs
            {
                Memory = 128,
                Cpu = 512,
                Image = image.ImageUri,
                Essential = true,
                PortMappings = new TaskDefinitionPortMappingArgs
                {
                    ContainerPort = 80,
                    TargetGroup = lb.DefaultTargetGroup
                },
                Environment  = new TaskDefinitionKeyValuePairArgs
                {
                    Name = "ASPNETCORE_ENVIRONMENT",
                    Value = "Development"
                }  
            }
        }
    });

    return new Dictionary<string, object?>
    {
        ["Public URL"] = Output.Format($"http://{lb.LoadBalancer.Apply(x => x.DnsName)}/swagger/index.html")
    };
});