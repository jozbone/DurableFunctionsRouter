using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.Azure.WebJobs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet;

namespace Router
{
    public class CreateOrUpdateFunctionAppOrchestrator
    {
        private readonly IAzure _azure;
        public CreateOrUpdateFunctionAppOrchestrator(IAzure azure)
        {
            _azure = azure;
        }
        [FunctionName("CreateOrUpdateFunctionAppOrchestrator")]
        public async Task<AppContext> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var appContext = context.GetInput<AppContext>();

            // Get the current context
            var currentAppContext =
                await context.CallEntityAsync<AppContext>(
                    new EntityId(nameof(FunctionApp), 
                        appContext.Name), 
                    "GetCurrent", 
                    null);

            if (currentAppContext == null || appContext.Version.RequireNewDeployment(currentAppContext.Version))
            {
                // New Deployment
                int counter = await context.CallEntityAsync<int>(
                    new EntityId(nameof(FunctionApp), 
                        appContext.Name),
                    "IncrementCounter", null);
                appContext.FunctionAppName = $"{appContext.Name}{counter}";
                appContext.ResourceGroup = $"{appContext.ResourceGroup}{counter}";

                await context.CallActivityAsync("CreateOrUpdateFunctionAppActivity", appContext);

                appContext.State = State.Creating;
                await context.CallEntityAsync(
                    new EntityId(nameof(FunctionApp), 
                        appContext.Name), 
                    "CreateOrUpdate", 
                    appContext);

                var state = "";
                while (state != "Succeeded" && state != "Failed" && state != "Cancelled")
                {
                    await context.CreateTimer(DateTime.UtcNow.AddSeconds(10), CancellationToken.None);
                    state = await context.CallActivityAsync<string>("CheckStatusActivity", appContext);
                }

                State provisioningState = state == "Succeeded" ? State.Running : State.Failed;
                appContext.State = provisioningState;

                await context.CallEntityAsync(
                    new EntityId(nameof(FunctionApp), 
                        appContext.Name), 
                    "CreateOrUpdate", 
                    appContext);

                return appContext;
            }
            else
            {
                // Update entity and return
                currentAppContext.Version = appContext.Version;
                await context.CallEntityAsync(
                    new EntityId(nameof(FunctionApp),
                        appContext.Name),
                    "CreateOrUpdate",
                    currentAppContext);
                return currentAppContext;
            }
        }

        [FunctionName("CreateOrUpdateFunctionAppActivity")]
        public async Task CreateOrUpdateFunctionAppActivity(
            [ActivityTrigger] AppContext context)
        {
            JObject parametersObjectv1 = new JObject(
                new JProperty("AppName",
                    new JObject(
                        new JProperty("value", context.FunctionAppName)
                    )
                ),
                new JProperty("AppInsightsLocation",
                    new JObject(
                        new JProperty("value", context.AppInsightsLocation)
                    )
                )
            );
            // var parameter = JsonConvert.SerializeObject(new TemplateParameter(context.FunctionAppName));
            // var parameters = new TemplateParameter[2];
            // parameters[0] = new TemplateParameter(context.FunctionAppName);
            // parameters[1] = new TemplateParameter(context.AppInsightsLocation);
            // var parameter = JsonConvert.SerializeObject(parameters);
            var deployment = await _azure.Deployments.Define(context.ResourceGroup)
                .WithNewResourceGroup(context.ResourceGroup, Region.Create(context.Region))
                .WithTemplateLink(
                    "https://raw.githubusercontent.com/Azure/azure-quickstart-templates/master/quickstarts/microsoft.web/function-app-create-dynamic/azuredeploy.json",
                    "1.0.0.0")
                .WithParameters(parametersObjectv1)
                .WithMode(DeploymentMode.Complete)
                .BeginCreateAsync();
        }

        [FunctionName("CheckStatusActivity")]
        public async Task<string> CheckStatusActivity(
            [ActivityTrigger] AppContext context)
        {
            var deployment =
                await _azure.Deployments.GetByResourceGroupAsync(context.ResourceGroup, context.ResourceGroup);
            return deployment.ProvisioningState;
        }
    }
}