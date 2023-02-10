using Newtonsoft.Json;
using Penguin.Cms.Repositories;
using Penguin.DependencyInjection.Abstractions.Attributes;
using Penguin.DependencyInjection.Abstractions.Enums;
using Penguin.Email.Abstractions.Interfaces;
using Penguin.Email.Templating.Abstractions.Interfaces;
using Penguin.Messaging.Core;
using Penguin.Persistence.Abstractions.Interfaces;
using Penguin.Templating.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Penguin.Cms.Email.Templating.Repositories
{
    /// <summary>
    /// An IRepository implementation for managing email templates and generating emails from them
    /// </summary>
    [Register(ServiceLifetime.Scoped, typeof(ISendTemplates))]
    public class EmailTemplateRepository : AuditableEntityRepository<EmailTemplate>, ISendTemplates
    {
        /// <summary>
        /// An IEmailRenderer implementation used to bind the template as well as output HTML
        /// </summary>
        protected IEmailTemplateRenderer EmailRenderer { get; set; }

        /// <summary>
        /// An IRepository information used to persist generated email messages
        /// </summary>
        protected IQueueAndSendMail EmailRepository { get; set; }

        /// <summary>
        /// Creates a new instance of this repository
        /// </summary>
        /// <param name="dbContext">The persistance context to be used when accessing templates</param>
        /// <param name="emailRepository">An IRepository information used to persist generated email messages</param>
        /// <param name="emailRenderer">An IEmailRenderer implementation used to bind the template as well as output HTML</param>
        /// <param name="messageBus">An optional message bus for sending EmailTemplate messages</param>
        public EmailTemplateRepository(IPersistenceContext<EmailTemplate> dbContext, IQueueAndSendMail emailRepository, IEmailTemplateRenderer emailRenderer, MessageBus messageBus = null) : base(dbContext, messageBus)
        {
            EmailRenderer = emailRenderer;
            EmailRepository = emailRepository;
        }

        /// <summary>
        /// Generates an email using the template(s) assigned to the given handler name
        /// </summary>
        /// <param name="Model">Binding Parameters. First is property name, second is value. Value can be string.</param>
        /// <param name="SendDate">The date the email should be queued to send on</param>
        /// <param name="HandlerName">The name of the handler generating this email. If null, an attempt is made to retrieve this value using the stack</param>
        /// <param name="skipCallerValidation">For when anything other than an action is calling the method</param>
        /// <param name="Overrides">An email message whos non-default values override the generated template values, useful for debugging by altering the output</param>
        public void GenerateEmailFromTemplate(Dictionary<string, object> Model, DateTime? SendDate = null, string HandlerName = null, bool skipCallerValidation = false, IEmailMessage Overrides = null)
        {
            if (Model is null)
            {
                throw new ArgumentNullException(nameof(Model));
            }

            List<TemplateParameter> templateParameters = new(Model.Count);
            StackInformation stackInformation = new(new StackTrace(), HandlerName);

            foreach (KeyValuePair<string, object> kvp in Model)
            {
                string name = kvp.Key;
                object value = kvp.Value;
                Type type = null;
                if (kvp.Value is null)
                {
                    if (!stackInformation.CallingMethodParameters.TryGetValue(kvp.Key, out Type paramType))
                    {
                        throw new ArgumentNullException($"Template parameter {kvp.Key} is null and additionally no parameter with a matching name can be found on the parent stack frame to infer the parameter type. You can specify the type yourself by using the overload that accepts an IEnumerable of template parameters");
                    }

                    type = paramType;
                }
                else
                {
                    type = kvp.Value.GetType();
                }

                templateParameters.Add(new TemplateParameter(type, name, value));
            }

            if (!skipCallerValidation)
            {
                stackInformation.ValidateMethodParameters(templateParameters);
            }

            GenerateEmailFromTemplate(templateParameters, SendDate, stackInformation.HandlerName, true, Overrides);
        }

        /// <summary>
        /// Generates an email using the template(s) assigned to the given handler name
        /// </summary>
        /// <param name="parameters">Binding Parameters. First is property name, second is value. Value can be string.</param>
        /// <param name="SendDate">The date the email should be queued to send on</param>
        /// <param name="HandlerName">The name of the handler generating this email. If null, an attempt is made to retrieve this value using the stack</param>
        /// <param name="skipCallerValidation">For when anything other than an action is calling the method</param>
        /// <param name="Overrides">An email message whos non-default values override the generated template values, useful for debugging by altering the output</param>
        public void GenerateEmailFromTemplate(List<TemplateParameter> parameters, DateTime? SendDate = null, string HandlerName = null, bool skipCallerValidation = false, IEmailMessage Overrides = null)
        {
            if (parameters is null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (SendDate == null)
            {
                SendDate = DateTime.Now;
            }

            if (HandlerName is null || !skipCallerValidation)
            {
                StackInformation stackInformation = new(new StackTrace(), HandlerName);
                HandlerName = stackInformation.HandlerName;

                if (!skipCallerValidation)
                {
                    stackInformation.ValidateMethodParameters(parameters);
                }
            }

            List<EmailTemplate> templates = GetEnabledTemplates(HandlerName);

            foreach (EmailTemplate thisTemplate in templates.Select(t => JsonConvert.DeserializeObject<EmailTemplate>(JsonConvert.SerializeObject(t)))) //Detatch the templates (Without losing child properties) so we can overwrite with post-transform values
            {
                EmailMessage thisMessage = new();

                foreach (PropertyInfo templateProperty in thisTemplate.GetType().GetProperties().Where(p => p.PropertyType == typeof(string)))
                {
                    string value = EmailRenderer.RenderEmail(parameters, thisTemplate, templateProperty);

                    templateProperty.SetValue(thisTemplate, value);
                }

                JsonConvert.PopulateObject(JsonConvert.SerializeObject(thisTemplate), thisMessage);

                //Check for an override object. Created so that the recipient of a template can be overridden
                //Null fields arent serialized and so values that arent set in the override should be ignored
                if (Overrides != null)
                {
                    JsonConvert.PopulateObject(JsonConvert.SerializeObject(Overrides, new JsonSerializerSettings()
                    {
                        DefaultValueHandling = DefaultValueHandling.Ignore
                    }), thisMessage);
                }

                //Make sure we create a new entity
                thisMessage._Id = 0;
                thisMessage.Guid = Guid.NewGuid();
                thisMessage.ExternalId = thisMessage.Guid.ToString();
                thisMessage.Label = HandlerName;

                using IWriteContext writeContext = WriteContext();
                EmailRepository.QueueOrSend(thisMessage);
            }
        }

        /// <summary>
        /// returns a list of all enabled templates for a particular handler
        /// </summary>
        /// <param name="handlerName">The handler to retrieve email templates for</param>
        /// <returns>a list of all enabled templates for a particular handler</returns>
        public List<EmailTemplate> GetEnabledTemplates(string handlerName)
        {
            return this.Where(e => e.HandlerName == handlerName && e.Enabled).ToList();
        }
    }
}