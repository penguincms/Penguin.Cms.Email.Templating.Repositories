﻿using Penguin.Cms.Email.Abstractions.Attributes;
using Penguin.Email.Abstractions.Interfaces;
using Penguin.Templating.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Penguin.Cms.Email.Templating.Repositories
{
    internal class StackInformation
    {
        private const string MESSAGE_HANDLER_NOT_IMPLEMENTED_MESSAGE = "Calling method requires MessageHandlerAttribute to use this function";

        public MethodBase CallingMethod { get; set; }

        public Dictionary<string, Type> CallingMethodParameters { get; set; }

        public EmailHandlerAttribute Handler { get; set; }

        public string HandlerName { get; set; }

        public StackInformation(StackTrace callingStackTrace, string handlerName = null)
        {
            int f = 0;
            do
            {
                CallingMethod = callingStackTrace.GetFrame(f++).GetMethod();
                Handler = CallingMethod.GetCustomAttribute<EmailHandlerAttribute>();
            } while (Handler is null);

            HandlerName = handlerName ?? Handler?.HandlerName ?? $"{CallingMethod?.DeclaringType?.Name}.{CallingMethod?.Name}";
            CallingMethodParameters = CallingMethod.GetParameters().ToDictionary(k => k.Name, p => p.ParameterType);
        }

        //By default we want to ensure no parameters are missing from the calling method.
        //This was set up for the Action based templating. Theres probably a better way now.
        //Its used to enforce standards on the calling method to reduce errors
        public void ValidateMethodParameters(List<TemplateParameter> Parameters)
        {
            if (Handler == null)
            {
                throw new ArgumentNullException(nameof(Parameters), MESSAGE_HANDLER_NOT_IMPLEMENTED_MESSAGE);
            }

            Type DeclaringType = CallingMethod.DeclaringType;

            if (!DeclaringType.GetInterfaces().Contains(typeof(IEmailHandler)))
            {
                throw new Exception($"{DeclaringType.Name} must implement {nameof(IEmailHandler)} to use this function");
            }

            foreach (ParameterInfo thisParameter in CallingMethod.GetParameters())
            {
                if (!Parameters.Any(p => p.Name == thisParameter.Name))
                {
                    throw new ArgumentNullException($"{thisParameter.Name} must be passed into template generation");
                }
            }

            foreach (TemplateParameter param in Parameters)
            {
                if (!CallingMethod.GetParameters().Any(p => p.Name == param.Name))
                {
                    throw new ArgumentNullException($"{param.Name} passed into email generation, but does not appear in parameters list");
                }
            }
        }
    }
}