using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System.Web;
using System.Net.Mail;
using System.Net;

namespace WorkflowAzFunc
{
    public static class Function1
    {
        [FunctionName("SubmitForApproval_Orchestrator")]
        public static async Task RunOrchestrator(
        [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            log.LogInformation($"************** SubmitForApproval Orchestrator method executing ********************");

            var (addUserRequest, requestUri1) = context.GetInput<Tuple<AddProjectRequest, Uri>>();
            var requestUri = requestUri1;
            var addProjectRequest = addUserRequest;
            addProjectRequest.WorkFlowStatus = "Draft";
            var ProcesserRequest = new ProcesserRequest
            {
                AddProjectRequest = addProjectRequest,
                ApproveRequestUrl = $"{requestUri.Scheme}://{requestUri.Host}:{requestUri.Port}/api/ProjectRequestForApprove?id={context.InstanceId}",
                SendForRevisionUrl = $"{requestUri.Scheme}://{requestUri.Host}:{requestUri.Port}/api/ProjectRequestSendForRevision?id={context.InstanceId}",
            };
            await context.CallActivityAsync("SendForApproval", ProcesserRequest);

            using (var timeout = new CancellationTokenSource())
            {
                DateTime AdminDeadline = context.CurrentUtcDateTime.AddDays(5); 

                Task durableTimeout = context.CreateTimer(AdminDeadline, timeout.Token);

                Task<bool> AdminEvent = context.WaitForExternalEvent<bool>("Moderation");

                if (AdminEvent == await Task.WhenAny(AdminEvent, durableTimeout))
                {
                    timeout.Cancel();

                    bool isApproved = AdminEvent.Result;

                    if (isApproved)
                    {
                        ProcesserRequest.AddProjectRequest.WorkFlowStatus = "Approve";
                        log.LogInformation($"************** Project Request '{ProcesserRequest.AddProjectRequest.ProjectId}'  was approved by a Admin/owner ********************");
                    }
                    else
                    {
                        ProcesserRequest.AddProjectRequest.WorkFlowStatus = "Draft";
                        log.LogInformation($"************** Project Request '{ProcesserRequest.AddProjectRequest.ProjectId}' was send for revision by a Admin/owner ********************");
                    }
                }
                else
                {
                    log.LogInformation($"************** Project Request '{ProcesserRequest.AddProjectRequest.ProjectId}'   was not reviewed by a Admin/owner in time, escalating...  ********************");
                    await context.CallActivityAsync("SendMail",ProcesserRequest);
                }
            }

            log.LogInformation($"************** Project Request Orchestration instance {context.InstanceId} complete ********************");
        }

        [FunctionName("SendForApproval")]
        public static string RequestApproval([ActivityTrigger] ProcesserRequest processerRequest, ILogger log)
        {

            processerRequest.AddProjectRequest.WorkFlowStatus = "SubmitForApproval";
            log.LogInformation($" Send Mail {processerRequest.AddProjectRequest.ProjectId} \r\nApprove: {processerRequest.ApproveRequestUrl} \r\nSend For Revision: {processerRequest.SendForRevisionUrl}");
            return $" Send Mail {processerRequest.AddProjectRequest.ProjectId} \r\nApprove: {processerRequest.ApproveRequestUrl} \r\nSend For Revision: {processerRequest.SendForRevisionUrl}!";
        }
        [FunctionName("SendMail")]
        public static string SendMail([ActivityTrigger] ProcesserRequest processerRequest, ILogger log)
        {

            log.LogInformation($" Send Mail Wait for response long time");
            return $" Send Mail Wait for response long time";
        }

        [FunctionName("ProjectRequestForApprove")]
        public static async Task<IActionResult> ProjectRequestForApprove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
        [DurableClient] IDurableOrchestrationClient client,
        ILogger log)
        {
            var addProjectRequest = await req.Content.ReadAsAsync<AddProjectRequest>();

            var id = HttpUtility.ParseQueryString(req.RequestUri.Query).Get("id");

            // await context.CallActivityAsync("", ProcesserRequest);
            var status = await client.GetStatusAsync(id);

            if (status.RuntimeStatus == OrchestrationRuntimeStatus.Running)
            {
                await client.RaiseEventAsync(id, "Moderation", true);
                return new OkObjectResult("Add Project Request was Approved.");
            }

            return new OkObjectResult("OrchestrationRuntimeStatus is already completed please create new instance");
        }

        [FunctionName("ProjectRequestSendForRevision")]
        public static async Task<IActionResult> ProjectRequestSendForRevision(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
        [DurableClient] IDurableOrchestrationClient client,
        ILogger log)
        {
            var addProjectRequest = await req.Content.ReadAsAsync<AddProjectRequest>();

            var id = HttpUtility.ParseQueryString(req.RequestUri.Query).Get("id");

            var status = await client.GetStatusAsync(id);

            if (status.RuntimeStatus == OrchestrationRuntimeStatus.Running)
            {
                await client.RaiseEventAsync(id, "Moderation", false);
                return new OkObjectResult("Add Project Request was Send for Revision.");
            }

            return new OkObjectResult("OrchestrationRuntimeStatus is already completed please create new instance");
        }

       
        [FunctionName("ProjectOrchestration")]
        public static async Task<HttpResponseMessage> HttpStart(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
        [DurableClient] IDurableOrchestrationClient starter,
        ILogger log)
        {
            var addProjectRequest = await req.Content.ReadAsAsync<AddProjectRequest>();

            addProjectRequest.WorkFlowStatus = "Draft";

            string instanceId = await starter.StartNewAsync("SubmitForApproval_Orchestrator", null, (addUserRequest:addProjectRequest, requestUri1:req.RequestUri));

            log.LogInformation($"Started Project orchestration with ID = '{instanceId}'.");

            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted)
            {
                Content = new StringContent("Your Project Request has been submitted and is awaiting Admin/Owner Response. Project Id - "+addProjectRequest.ProjectId+" Instance Id- " + instanceId +" Work Flow Status "+addProjectRequest.WorkFlowStatus)
            };
        }
    }
    public class AddProjectRequest
    {
        public string ProjectId { get; set; }
        public string Name { get; set; }
        public string Commands { get; set; }
        public string WorkFlowStatus { get; set; }
    }
    public class asd
    {
        public string ProjectId { get; set; }
    }
    public class ProcesserRequest
    {
        public AddProjectRequest AddProjectRequest { get; set; }
        public string ApproveRequestUrl { get; set; }
        public string SendForRevisionUrl { get; set; }
    }
}