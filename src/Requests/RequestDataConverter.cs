﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RapidCore.Reflection;
using Skarp.HubSpotClient.Interfaces;

namespace Skarp.HubSpotClient.Requests
{
    public class RequestDataConverter
    {
        private readonly ILogger<RequestDataConverter> _logger;

        public RequestDataConverter(
            ILogger<RequestDataConverter> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Converts the given <paramref name="entity"/> to a hubspot data entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns></returns>
        public HubspotDataEntity ToHubspotDataEntity(IHubSpotEntity entity)
        {
            _logger.LogDebug("Convert ToHubspotDataEntity");
            var mapped = new HubspotDataEntity();
            bool isv2Route = entity.RouteBasePath.Contains("/v2");
            _logger.LogDebug("isv2route: {0}", isv2Route);

            var allProps = entity.GetType().GetProperties();
            _logger.LogDebug("Have {0} props to map", allProps.Length);

            foreach (var prop in allProps)
            {
                _logger.LogDebug("Mapping prop: '{0}'", prop.Name);
                if (prop.Name.Equals("RouteBasePath")) continue;

                var propValue = prop.GetValue(entity);
                var item = new HubspotDataEntityProp
                {
                    Property = prop.Name,
                    Value = propValue?.ToString()
                };

                if (isv2Route)
                {
                    item.Property = null;
                    item.Name = prop.Name;
                }
                if (item.Value == null) continue;

                mapped.Properties.Add(item);
            }

            _logger.LogDebug("Mapping complete, returning data");
            return mapped;
        }

        /// <summary>
        /// Convert from the dynamicly typed <see cref="ExpandoObject"/> into a strongly typed <see cref="IHubSpotEntity"/>
        /// </summary>
        /// <param name="dynamicObject">The <see cref="ExpandoObject"/> representation of the returned json</param>
        /// <returns></returns>
        public T FromHubSpotResponse<T>(ExpandoObject dynamicObject) where T : IHubSpotEntity, new()
        {
            var data = (T)ConvertSingleEntity(dynamicObject, new T());
            return data;
        }

        public T FromHubSpotListResponse<T>(ExpandoObject dynamicObject) where T : IHubSpotEntity, new()
        {
            // get a handle to the underlying dictionary values of the ExpandoObject
            var expandoDict = (IDictionary<string, object>)dynamicObject;

            // For LIST contacts the "contacts" property should be populated, for LIST companies the "companies" property should be populated, and so on
            // in our T item, search for a property that is an IList<IHubSpotEntity> and use that as our prop name selector into the DynamoObject.....
            // So on the IContactListHubSpotEntity we have a IList<IHubSpotEntity> Contacts - find that prop, lowercase to contacts and that prop should
            // be in the DynamoObject from HubSpot! Tricky stuff
            var targetType = typeof(IHubSpotEntity);
            var data = new T();
            var dataProps = data.GetType().GetProperties();
            var dataTargetProp = dataProps.SingleOrDefault(p => targetType.IsAssignableFrom(p.PropertyType.GenericTypeArguments.FirstOrDefault()));

            if (dataTargetProp == null)
            {
                throw new ArgumentException("Unable to locate a property on the data class that implements IList<T> where T is a IHubSpotEntity");
            }

            var propSerializedName = dataTargetProp.GetPropSerializedName();
            if (!expandoDict.ContainsKey(propSerializedName))
            {
                throw new ArgumentException($"The json data does not contain a property of name {propSerializedName} which is required to decode the json data");
            }

            // Find the generic type for the List in question
            var genericEntityType = dataTargetProp.PropertyType.GenericTypeArguments.First();
            // get a handle to Add on the list (actually from ICollection)
            var listAddMethod = dataTargetProp.PropertyType.FindMethodRecursively("Add", genericEntityType);
            // Condensed version of : https://stackoverflow.com/a/4194063/1662254
            var listInstance =
                Activator.CreateInstance(typeof(List<>).MakeGenericType(genericEntityType));
            if (listAddMethod == null)
            {
                throw new ArgumentException("Unable to locate Add method on the list of items to deserialize into - is it an IList?");
            }

            // Convert all the entities
            var jsonEntities = expandoDict[propSerializedName];
            foreach (var entry in jsonEntities as List<object>)
            {
                // convert single entity
                var expandoEntry = entry as ExpandoObject;
                var dto = ConvertSingleEntity(expandoEntry, Activator.CreateInstance(genericEntityType));
                // add entity to list
                listAddMethod.Invoke(listInstance, new[] { dto });
            }
            // assign our reflected list instance onto the data object
            dataTargetProp.SetValue(data, listInstance);

            var allPropNamesInSerializedFormat = GetAllPropsWithSerializedNameAsKey(dataProps);
            // Now try to map any remaining props from the dynamo object into the response dto we shall return
            foreach (var kvp in expandoDict)
            {
                // skip the property with all the items for the response as we have already mapped that
                if (kvp.Key == propSerializedName) continue;

                // The Key of the current item should be mapped, so we have to find a property in the target dto that "Serializes" into this value...
                if (!allPropNamesInSerializedFormat.TryGetValue(kvp.Key, out PropertyInfo theProp))
                {
                    continue;
                }
                // we have a property which name serializes to the kvp.Key, let's set the data
                theProp.SetValue(data, kvp.Value);
            }

            return data;
        }

        private IDictionary<string, PropertyInfo> GetAllPropsWithSerializedNameAsKey(PropertyInfo[] dataProps)
        {
            var dict = new Dictionary<string, PropertyInfo>();
            foreach (var prop in dataProps)
            {
                var propName = prop.GetPropSerializedName();
                dict.Add(propName, prop);
            }
            return dict;
        }

        /// <summary>
        /// Converts a single "dynamic" representation of an entity into a typed entity
        /// </summary>
        /// <remarks>
        /// The dynamic object being passed in should have a prop called "properties" which contains all the object properties to map, as well
        /// as vid and other root level objects stored in the HubSpot JSON response
        /// </remarks>
        /// <param name="dynamicObject">An <see cref="ExpandoObject"/> instance that contains a single HubSpot entity to deserialize</param>
        /// <param name="dto">An instantiated DTO that shall recieve data</param>
        /// <returns>The populated DTO</returns>
        internal object ConvertSingleEntity(ExpandoObject dynamicObject, object dto)
        {
            var expandoDict = (IDictionary<string, object>)dynamicObject;
            var dtoProps = dto.GetType().GetProperties();

            // The Properties object in the json / response data contains all the props we wish to map - if that does not exist
            // we cannot proceeed
            if (!expandoDict.TryGetValue("properties", out var dynamicProperties))
            {
                throw new ArgumentException("The given JSON document does not contain a properties object");
            }

            // The vid is the "id" of the entity
            if (expandoDict.TryGetValue("vid", out var vidData))
            {
                var vidProp = dtoProps.SingleOrDefault(q => q.Name.ToLowerInvariant() == "vid");
                vidProp?.SetValue(dto, vidData);
            }

            foreach (var dynamicProp in dynamicProperties as ExpandoObject)
            {
                // prop.Key contains the name of the property we wish to map into the DTO
                // prop.Value contains the data returned by HubSpot, which is also an object 
                // in there we need to go get the "value" prop to get the actual value
                _logger.LogDebug("Looking at dynamic prop: {0}", dynamicProp.Key);

                if (!((IDictionary<string, Object>)dynamicProp.Value).TryGetValue("value", out object dynamicValue))
                {
                    continue;
                }

                var targetProp =
                    dtoProps.SingleOrDefault(q => q.Name.ToLowerInvariant() == dynamicProp.Key.ToLowerInvariant());
                _logger.LogDebug("Have target prop? '{0}' with name: '{1}' and actual value: '{2}'", targetProp != null,
                    targetProp?.Name ?? "N/A", dynamicValue);
                targetProp?.SetValue(dto, dynamicValue);
            }
            return dto;
        }

    }
}