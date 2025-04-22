using Microsoft.AspNetCore.SignalR;
using Azure;
using Azure.AI.Inference;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Chat.AI.ChatHub
{
    public class ChatHub : Hub
    {
        private readonly ChatCompletionsClient _chatClient;
        private readonly IReadOnlyList<ChatCompletionsToolDefinition> _tools;
        private readonly string _modelName = "gpt-4o";
        private readonly HttpClient _httpClient;
        private static readonly ConcurrentDictionary<string, List<ChatRequestMessage>> _userHistories = new();
        private static readonly ConcurrentDictionary<string, List<AttributeDefinition>> _attributeDefinitionsCache = new();

        private readonly IConfiguration _config;

        private const string API_KEY = "8jShS8HuW0L0lPlp5tEJMNsJxg0UhnorajE0m0zoqrwidqPjTg3OJQQJ99BBACLArgHXJ3w3AAAAACOG5UyB";
        private const string API_ENDPOINT = "https://iaaztest8966716774.services.ai.azure.com/models";
        private const string TOKEN = "eyJraWQiOiJjbDdDVTZZdllyTm95bE9WTGFhYkgtVE1ETmVEeFE3bDZnSWdJR0xUWXhBIiwiYWxnIjoiUlMyNTYifQ.eyJ2ZXIiOjEsImp0aSI6IkFULjZQNFlyclRUbkxEcVFZcXFFbThpWWVRSjdIaC0zYmFEQ1lQR2FDX21JclkiLCJpc3MiOiJodHRwczovL2Fzc3VyYW50Lm9rdGFwcmV2aWV3LmNvbS9vYXV0aDIvZGVmYXVsdCIsImF1ZCI6ImFwaTovL2RlZmF1bHQiLCJpYXQiOjE3NDUzMTMzNDcsImV4cCI6MTc0NTMyMDU0NywiY2lkIjoiMG9hc2F0b3k1ZXljNHZTbFYwaDciLCJzY3AiOlsiYXBpX3Njb3BlIl0sInN1YiI6IjBvYXNhdG95NWV5YzR2U2xWMGg3In0.D16EG73ReGucMvmtIeTp4KDgwkmDjeFSYrg4oTZhDR7tM_kL3AxJTjsf4zEf7imNp0UoqbSiDiPaYlopUGuTLxjJ8eVjr5insY1yfUDYXVnphbdfMFaMteFOgs5oi5kiw9YeDoa4tqCuA1cEzV18RTXar4zu7iyaoY64zqo2jvkowVZ3DnIbZV_BcJyJJx10t9Wv5GYZ6tfc0iUAXTNtO4O8DphdYw5gVWvkEyPtU10eMlu4GTMHVdhN7KiTVSEjbxHQhTRY9zFhDydWy2HFvNddNgf-vQpvphRnAKMlO6Mh8LzaLQ_q2JzDZj9fc_VBHqMbWPmp04rsrbK4ur-GUQ";
        private const string SUBSCRIPTION_KEY = "76e8ee7b5d9541e0ada85a1aefeb52ac";

        private const string SystemMessage = @"Eres un asistente especializado en gestión de reclamos. Sigue ESTE FLUJO ESTRICTAMENTE:

1. Obtener certificados con get_certificates usando N° documento
2. Mostrar cada certificado/poliza y cobertura para que luego pueda seleccionar de manera sencilla y facil de leer, puede ser en una List
3. Luego de Seleccionar Certificado/Cobertura:
   a. Obtener coberturas con get_product_coverages (agencyCode='00001')
   b. Para cada cobertura obtenida:
      i. Obtener requisitos OBLIGATORIOS con get_claim_requirements que se invoca por branch y coverage.Code
      ii. Mostrar al usuario CADA FieldDescription de los atributos requeridos de forma secuencial
      iii. Solicitar UN atributo a la vez en el orden especificado
      iv. Tratar de no repetir atributos que ya fueron completados
4. Validar que todos los atributos requeridos estén completos y darle guía al usuario en caso de no entender
5. Crear el siniestro / reclamo una vez que la validación haya resultado correcta con TODOS los atributos definidos desde la configuración por cobertura.

Reglas CRÍTICAS:
- Usar get_claim_requirements INMEDIATAMENTE después de get_product_coverages
- Los parámetros para get_claim_requirements deben ser:
   branchCode: branchCode de get_product_coverages
   coverageCode: Code de la cobertura seleccionada
- Guiar al usuario usando EXCLUSIVAMENTE los FieldDescription de los atributos
- Identificar los atributos que ya se completaron y no volver a solicitarlos (los atributos con fieldName cargados)
- No avanzar al siguiente atributo hasta tener el actual completo
- NO Tomar valores por defecto, el cliente debe responder siempre con los atributos requeridos, pero también indicarle los opcionales
- Una vez completados los atributos y sus datos, avanzar con la creación del siniestro con los atributos especificados.
  *Debes pasar los atributos identificados para poder dar de alta el reclamo/siniestro*";



        public ChatHub(IConfiguration config)
        {
            _config = config;

            var endpoint = new Uri("https://iaaztest8966716774.services.ai.azure.com/models");
            var key = "8jShS8HuW0L0lPlp5tEJMNsJxg0UhnorajE0m0zoqrwidqPjTg3OJQQJ99BBACLArgHXJ3w3AAAAACOG5UyB";
            _chatClient = new ChatCompletionsClient(endpoint, new AzureKeyCredential(key), new AzureAIInferenceClientOptions());
            _httpClient = new HttpClient();

            _tools = new List<ChatCompletionsToolDefinition>
{
    new ChatCompletionsToolDefinition(CreateGetCertificatesFunction()),
    new ChatCompletionsToolDefinition(CreateGetProductCoveragesFunction()),
    new ChatCompletionsToolDefinition(CreateClaimRequirementsFunction()),
    new ChatCompletionsToolDefinition(CreateCreateClaimFunction())
}.AsReadOnly();
        }

        public async Task SendMessage(string user, string message)
        {
            const int maxIterations = 3;
            if (!_userHistories.TryGetValue(user, out var messages))
            {
                messages = new List<ChatRequestMessage>
        {
            new ChatRequestSystemMessage(SystemMessage)
        };
                _userHistories[user] = messages;  // Guardamos el historial en el diccionario
            }

            // Agregar el mensaje del usuario al historial
            messages.Add(new ChatRequestUserMessage(message));

            string finalReply = "No se pudo completar la solicitud";

            for (var i = 0; i < maxIterations; i++)
            {
                try
                {
                    ////TruncateMessageHistory(messages);

                    var requestOptions = new ChatCompletionsOptions
                    {
                        Model = _modelName,
                        Messages = messages,
                        ToolChoice = ChatCompletionsToolChoice.Auto
                    };

                    requestOptions.Tools.Clear();
                    foreach (var tool in _tools) requestOptions.Tools.Add(tool);

                    var response = await _chatClient.CompleteAsync(requestOptions);
                    var completion = response.Value;

                    // Truncar TODOS los IDs antes de cualquier uso
                    var processedToolCalls = completion.ToolCalls.Select(tc =>
                        new ChatCompletionsToolCall(
                            id: SafeTruncateId(tc.Id),
                            new FunctionCall(tc.Name, tc.Arguments)
                        )).ToList();

                    ValidateMessageHistory(messages);

                    if (!processedToolCalls.Any())
                    {
                        finalReply = completion.Content;
                        break;
                    }

                    // Añadir mensaje con IDs truncados
                    messages.Add(new ChatRequestAssistantMessage(
                        content: completion.Content,
                        toolCalls: processedToolCalls
                    ));

                    // Procesar herramientas con IDs truncados
                    var toolResponses = await ProcessToolCalls(processedToolCalls);
                    messages.AddRange(toolResponses);
                }
                catch (RequestFailedException ex) when (ex.ErrorCode == "string_above_max_length")
                {
                    finalReply = "Error interno: Por favor reintenta tu solicitud";
                    break;
                }
                catch (Exception ex)
                {
                    finalReply = $"Error crítico: {ex.Message}";
                    break;
                }
            }

            await Clients.Caller.SendAsync("ReceiveMessage", "Assistant", finalReply);

            _userHistories[user] = messages;
        }

        private FunctionDefinition CreateClaimRequirementsFunction()
        {
            return new FunctionDefinition("get_claim_requirements")
            {
                Description = "OBTENER REQUISITOS OBLIGATORIOS para reportar un incidente. Los parámetros branchCode y coverageCode deben obtenerse de la última respuesta de get_product_coverages.",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        branchCode = new
                        {
                            type = "string",
                            description = "Código de rama obtenido de get_product_coverages (branchCode)"
                        },
                        coverageCode = new
                        {
                            type = "string",
                            description = "Código de cobertura específico obtenido de get_product_coverages"
                        }
                    },
                    required = new[] { "branchCode", "coverageCode" }
                })
            };
        }

        private FunctionDefinition CreateGetCertificatesFunction()
        {
            return new FunctionDefinition("get_certificates")
            {
                Description = "Get customer policy certificates by document number",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        documentNumber = new { type = "string", description = "Customer's document number" },
                        page = new { type = "integer", description = "Page number to retrieve" }
                    },
                    required = new[] { "documentNumber" }
                })
            };
        }

        private FunctionDefinition CreateGetProductCoveragesFunction()
        {
            return new FunctionDefinition("get_product_coverages")
            {
                Description = "Obtener coberturas asociadas al producto para posteriormente abrir el reclamo analizando que cobertura es más conveniente",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        agencyCode = new { type = "string", description = "Agency code" },
                        productCode = new { type = "string", description = "Product code" }
                    },
                    required = new[] { "agencyCode", "productCode" }
                })
            };
        }

        private FunctionDefinition CreateCreateClaimFunction()
        {
            return new FunctionDefinition("report_incident")
            {
                Description = "Inicia el proceso para reportar un incidente cubierto por la póliza, tener en cuenta todos los atributos de cobertura identificados para abrir el reclamo",
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        agencyCode = new { type = "string" },
                        policyNumber = new { type = "string" },
                        certificateNumber = new { type = "number" },
                        coverageCode = new { type = "string" },
                        branchCode = new { type = "string" },
                        incidentDate = new { type = "string", format = "date-time" },
                        attributes = new
                        {
                            fieldName = "fieldName de los atributos de cobertura",
                            value = "Input obtenido del usuario"
                        }
                    },
                    required = new[] { "agencyCode", "policyNumber", "certificateNumber", "coverageCode", "branchCode", "incidentDate", "attributes" }
                })
            };
        }

        private async Task<object> GetClaimRequirements(string branchCode, string coverageCode)
        {
            try
            {
                var attributeDefs = await GetClaimAttributeDefinitions(branchCode, coverageCode);

                return new
                {
                    Instrucciones = "Debe proporcionar los siguientes datos OBLIGATORIOS:",
                    Requisitos = attributeDefs
                        .Where(a => a.IsRequired)
                        .Select(a => new
                        {
                            FieldName = a.FieldName,
                            Descripción = a.FieldDescription, // Usar FieldDescription como fuente primaria
                            Tipo = a.FieldType,
                            Opciones = a.Options?.Select(o => $"{o.Value}: {o.Description}")
                        })
                };
            }
            catch (Exception ex)
            {
                return new { Error = true, Message = $"Error obteniendo requisitos: {ex.Message}" };
            }
        }

        private static string SafeTruncateId(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Asegurar longitud máxima de 40 caracteres ASCII
            var cleanId = Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(input))
                .Replace("\0", "")
                .Trim();

            return cleanId.Length <= 40
                ? cleanId
                : cleanId.Substring(0, 40);
        }

        private async Task<List<ChatRequestToolMessage>> ProcessToolCalls(IEnumerable<ChatCompletionsToolCall> toolCalls)
        {
            var responses = new List<ChatRequestToolMessage>();
            foreach (var toolCall in toolCalls)
            {
                var toolResult = await ExecuteToolCall(toolCall);
                var safeId = SafeTruncateId(toolCall.Id);

                var responseJson = toolResult.StartsWith("{")
                    ? toolResult
                    : JsonSerializer.Serialize(new { Result = toolResult });

                responses.Add(new ChatRequestToolMessage(responseJson, safeId));
            }
            return responses;
        }

        private async Task<string> ExecuteToolCall(ChatCompletionsToolCall toolCall)
        {
            try
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Arguments.ToString());
                object result = toolCall.Name switch
                {
                    "get_certificates" => await GetCertificatesFromApi(
                        args["documentNumber"].ToString(),
                        args.TryGetValue("page", out var page) ? Convert.ToInt32(page) : 1),

                    "get_product_coverages" => await GetProductCoveragesFromApi(
                        args["agencyCode"].ToString(),
                        args["productCode"].ToString()),

                    "get_claim_requirements" => await GetClaimRequirements(
                        args["branchCode"].ToString(),
                        args["coverageCode"].ToString()),

                    "report_incident" => await CreateNewClaim(args),

                    _ => new { Error = $"Función no implementada: {toolCall.Name}" }
                };

                var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });

                if (resultJson.Length > 10000)
                {
                    resultJson = JsonSerializer.Serialize(new { Error = "Tool output too long" });
                }

                return resultJson;
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { Error = ex.Message });
            }
        }

        private void TruncateMessageHistory(List<ChatRequestMessage> messages, int maxTotalLength = 14000)
        {
            var systemMessage = messages.OfType<ChatRequestSystemMessage>().FirstOrDefault();
            var lastUserMessage = messages.OfType<ChatRequestUserMessage>().LastOrDefault();
            var lastAssistantMessage = messages.OfType<ChatRequestAssistantMessage>().LastOrDefault();
            var lastToolMessage = messages.OfType<ChatRequestToolMessage>().LastOrDefault();

            var retainedMessages = new List<ChatRequestMessage>();
            if (systemMessage != null) retainedMessages.Add(systemMessage);
            if (lastUserMessage != null) retainedMessages.Add(lastUserMessage);
            if (lastAssistantMessage != null) retainedMessages.Add(lastAssistantMessage);
            if (lastToolMessage != null) retainedMessages.Add(lastToolMessage);

            int totalLength = retainedMessages.Sum(m =>
            {
                return m switch
                {
                    ChatRequestUserMessage user => user.Content?.Length ?? 0,
                    ChatRequestAssistantMessage assistant => assistant.Content?.Length ?? 0,
                    ChatRequestToolMessage tool => tool.Content?.Length ?? 0,
                    _ => 0
                };
            });

            if (totalLength <= maxTotalLength)
            {
                messages.Clear();
                messages.AddRange(retainedMessages.Distinct());
                return;
            }

            throw new InvalidOperationException("El historial esencial supera el límite de tokens permitido.");
        }


        private void ValidateMessageHistory(List<ChatRequestMessage> messages)
        {
            foreach (var message in messages)
            {
                switch (message)
                {
                    case ChatRequestAssistantMessage assistantMsg:
                        foreach (var tc in assistantMsg.ToolCalls)
                        {
                            if (tc.Id?.Length > 40)
                                throw new InvalidOperationException($"Invalid ID length in history: {tc.Id}");
                        }
                        break;

                    case ChatRequestToolMessage toolMsg:
                        if (toolMsg.ToolCallId?.Length > 40)
                            throw new InvalidOperationException($"Invalid tool_call_id in response: {toolMsg.ToolCallId}");
                        break;
                }
            }
        }


        private async Task<object> GetCertificatesFromApi(string documentNumber, int page)
        {
            try
            {
                var url = $"https://apim-alborada-mod.azure-api.net/external/enrollment/getcertificates/{page}?documentNumber={documentNumber}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Add("Ocp-Apim-Subscription-Key", SUBSCRIPTION_KEY);
                request.Headers.Add("token", TOKEN);
                request.Headers.Add("X-ALBORADA-NET-AGENCY-CODE", "00001");

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    return new { Error = true, Message = $"Failed to fetch certificates: {response.StatusCode}" };
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize<JsonElement>(content);

                var certificates = new List<object>();
                foreach (var cert in json.GetProperty("items").EnumerateArray())
                {
                    var productCode = cert.GetProperty("productCode").GetString();
                    var productInfo = await GetProductCoveragesFromApi("00001", productCode);

                    // Ahora productInfo es de tipo ProductCoverageResponse
                    if (!productInfo.Error && productInfo.Coverages?.Count > 0)
                    {
                        // Añadir al contexto los requisitos de la primera cobertura
                        //var firstCoverage = productInfo.Coverages.First();
                        //var requirements = await GetClaimRequirements(
                        //    productInfo.BranchCode,
                        //    firstCoverage.Code
                        //);

                        certificates.Add(new
                        {
                            CertId = cert.GetProperty("certificateNumber").GetInt32(),
                            PolicyNumber = cert.GetProperty("policyNumber").GetString(),
                            HasClaims = cert.GetProperty("hasClaims").GetBoolean(),
                            ProductCode = cert.GetProperty("productCode").GetString(),
                            requirements = productInfo.Coverages,
                        });
                    }

                }

                return new { Items = certificates };
            }
            catch (Exception ex)
            {
                return new { Error = true, Message = $"Error al obtener los certificados: {ex.Message}" };
            }
        }

        public class ProductCoverageResponse
        {
            public string BranchCode { get; set; }
            public string ProductCode { get; set; }
            public string Product { get; set; }
            public List<CoverageInfo> Coverages { get; set; }
            public bool Error { get; set; }
            public string Message { get; set; }
        }


        public class CoverageInfo
        {
            public string Code { get; set; }
            public string Name { get; set; }
            public string BranchCode { get; set; }

            [JsonPropertyName("attributeDefinitions")]
            public List<AttributeDefinition> AttributeDefinitions { get; set; } // Nuevo campo
        }

        private async Task<ProductCoverageResponse> GetProductCoveragesFromApi(string agencyCode, string productCode)
        {
            try
            {
                var url = $"https://apim-alborada-mod.azure-api.net/external/policyemission/productv2/{agencyCode}/{productCode}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Add("Ocp-Apim-Subscription-Key", SUBSCRIPTION_KEY);
                request.Headers.Add("token", TOKEN);
                request.Headers.Add("X-ALBORADA-NET-AGENCY-CODE", agencyCode);

                var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    return new ProductCoverageResponse
                    {
                        Error = true,
                        Message = $"Error: {response.ReasonPhrase}",
                        Product = null,
                        Coverages = null
                    };
                }

                var content = await response.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize<JsonElement>(content);

                var anonymousConditionType = new[] {
                    new { Code = "", Description = "", Value = "" }
                };

                var coverages = json.GetProperty("coverages").EnumerateArray()
                .Select(cov => new CoverageInfo
                {
                    Code = cov.GetProperty("code").GetString(),
                    Name = cov.GetProperty("name").GetString(),
                    BranchCode = cov.GetProperty("branch").GetString()
                }).ToList();

                //foreach (var coverage in coverages)
                //{
                //    var attributes = await GetClaimAttributeDefinitions(agencyCode, coverage.Code);
                //    coverage.AttributeDefinitions = attributes;
                //}

                return new ProductCoverageResponse
                {
                    Product = json.GetProperty("commercialDescription").GetString(),
                    BranchCode = agencyCode,
                    ProductCode = productCode,
                    Coverages = coverages,
                    Error = false,
                    Message = null
                };
            }
            catch (Exception ex)
            {
                return new ProductCoverageResponse
                {
                    Error = true,
                    Message = $"Error: {ex.Message}",
                    Product = null,
                    Coverages = null
                };
            }
        }

        private async Task<object> CreateNewClaim(Dictionary<string, object> parameters)
        {
            try
            {
                // Validar parámetros básicos
                if (!parameters.ContainsKey("branchCode") || !parameters.ContainsKey("coverageCode"))
                    return new { Error = "Parámetros branchCode o coverageCode faltantes" };

                // Obtener definiciones de atributos
                var attributeDefs = await GetClaimAttributeDefinitions(
                    parameters["branchCode"].ToString(),
                    parameters["coverageCode"].ToString()
                );

                // Manejar atributos de usuario de forma segura
                Dictionary<string, object> userAttributes = new();

                if (parameters.TryGetValue("attributes", out object attributesValue))
                {
                    if (attributesValue is JsonElement jsonElement)
                    {
                        userAttributes = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                    }
                    else if (attributesValue is Dictionary<string, object> existingDict)
                    {
                        userAttributes = existingDict;
                    }
                }

                // Validar atributos
                var validation = ValidateClaimAttributes(attributeDefs, userAttributes);
                if (!validation.IsValid)
                {
                    return new
                    {
                        ErrorType = "MISSING_FIELDS",
                        Message = "Faltan datos requeridos:",
                        Campos = validation.MissingAttributes.Select(a => a.FieldDescription),
                        Instrucción = "Proporcione: " + string.Join(", ", validation.MissingAttributes.Select(a => a.FieldDescription))
                    };
                }

                var description = JsonSerializer.Serialize(
    validation.ValidAttributes.ToDictionary(
        kvp => kvp.Key,
        kvp =>
        {
            if (kvp.Value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => JsonDocument.Parse(element.GetRawText()).RootElement
                };
            }
            return kvp.Value;
        }
    )
);

                // Construir payload para API (manteniendo tipos correctos)
                var claimData = new
                {
                    agencyCode = GetString(parameters["agencyCode"]),
                    branchCode = GetString(parameters["branchCode"]),
                    policyNumber = GetString(parameters["policyNumber"]),
                    certificateNumber = GetInt(parameters["certificateNumber"]),
                    incidentDate = GetDate(parameters["incidentDate"]).ToString("o"),
                    coverageCode = GetString(parameters["coverageCode"]),
                    description = description
                };

                // Llamar a API
                var url = "https://apim-alborada-mod.azure-api.net/external/claim/NewClaim";

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(claimData, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                };

                request.Headers.Add("Ocp-Apim-Subscription-Key", SUBSCRIPTION_KEY);
                request.Headers.Add("token", TOKEN);

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                return new
                {
                    Success = response.IsSuccessStatusCode,
                    StatusCode = response.StatusCode,
                    Response = content
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    Error = true,
                    Message = $"Error al crear el siniestro: {ex.Message}",
                    Detalles = ex.InnerException?.Message
                };
            }
        }

        string GetString(object value) =>
    value is JsonElement e ? e.GetString() : value?.ToString();

        int GetInt(object value) =>
            value is JsonElement e ? e.GetInt32() : Convert.ToInt32(value);

        DateTime GetDate(object value) =>
            value is JsonElement e ? e.GetDateTime() : DateTime.Parse(value.ToString());

        private async Task<List<AttributeDefinition>> GetClaimAttributeDefinitions(string branchCode, string coverageCode)
        {
            try
            {
                if (_attributeDefinitionsCache.ContainsKey($"{branchCode}_{coverageCode}"))
                {
                    return _attributeDefinitionsCache[$"{branchCode}_{coverageCode}"];
                }

                var url = $"https://apim-alborada-mod.azure-api.net/external/claim/coverage-attribute-definition/{"42"}/{"C01"}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Add("Ocp-Apim-Subscription-Key", SUBSCRIPTION_KEY);
                request.Headers.Add("token", TOKEN);

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new List<AttributeDefinition>();
                }

                var attributes = JsonSerializer.Deserialize<List<AttributeDefinition>>(content);

                _attributeDefinitionsCache.TryAdd($"{branchCode}_{coverageCode}", attributes);

                return attributes;
            }
            catch (Exception ex)
            {
                return new List<AttributeDefinition>();
            }
        }

        private ValidationResult ValidateClaimAttributes(
        List<AttributeDefinition> attributeDefs,
        Dictionary<string, object> userAttributes)
        {
            var missingFields = new List<AttributeDefinition>();
            var validAttributes = new Dictionary<string, object>();

            foreach (var def in attributeDefs.Where(d => d.IsRequired))
            {
                if (!userAttributes.ContainsKey(def.FieldName) ||
                    string.IsNullOrWhiteSpace(userAttributes[def.FieldName]?.ToString()))
                {
                    missingFields.Add(def);
                }
                else
                {
                    validAttributes.Add(def.FieldName, userAttributes[def.FieldName]);
                }
            }

            return new ValidationResult
            {
                IsValid = missingFields.Count == 0,
                MissingAttributes = missingFields,
                ValidAttributes = validAttributes
            };
        }

        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public List<AttributeDefinition> MissingAttributes { get; set; }
            public Dictionary<string, object> ValidAttributes { get; set; }
        }


        // Clases para deserialización de atributos
        public class AttributeDefinition
        {
            [JsonPropertyName("fieldName")]
            public string FieldName { get; set; }

            [JsonPropertyName("fieldDescription")]
            public string FieldDescription { get; set; }  // Campo clave para guiar al usuario

            [JsonPropertyName("isRequired")]
            public bool IsRequired { get; set; }

            [JsonPropertyName("fieldType")]
            public string FieldType { get; set; }

            [JsonPropertyName("options")]
            public List<AttributeOption> Options { get; set; }
        }

        public class AttributeOption
        {
            [JsonPropertyName("value")]
            public object Value { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }
        }

    }
}
