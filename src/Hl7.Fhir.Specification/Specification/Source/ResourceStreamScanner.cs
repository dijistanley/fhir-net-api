﻿/* 
 * Copyright (c) 2016, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://github.com/ewoutkramer/fhir-net-api/blob/master/LICENSE
 */

using Hl7.Fhir.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Hl7.Fhir.Support;
using Hl7.Fhir.Model;
using Hl7.Fhir.Introspection;
using System.Diagnostics;

namespace Hl7.Fhir.Specification.Source
{
    /// <summary>
    /// Internal class which is able to scan a (possibly) large Xml FHIR (conformance) resource from a given stream
    /// </summary>
    internal class ResourceStreamScanner
    {
        private Stream _input;
        private string _origin;

        public ResourceStreamScanner(Stream input, string origin)
        {
            _input = input;
            _origin = origin;
        }

        public IEnumerable<ResourceScanInformation> List()
        {
            var resources = StreamResources();

            return resources
                .Select(res =>
                        new ResourceScanInformation()
                        {
                            ResourceType = EnumUtility.ParseLiteral<ResourceType>(res.Name.LocalName).Value,
                            ResourceUri =  fullUrl(res),
                            Canonical = getPrimitiveValueElement(res, "url"),
                            ValueSetSystem = getValueSetSystem(res),
                            UniqueIds = getUniqueIds(res),
                            ConceptMapSource = getCmSources(res),
                            ConceptMapTarget = getCmTargets(res),
                            Origin = _origin,
                        });
        }

        private string getValueSetSystem(XElement vs)
        {
            return vs.Elements(XmlNs.XFHIR + "codeSystem")
                     .Elements(XmlNs.XFHIR + "system")
                     .Attributes("value")
                     .Select(a => a.Value).SingleOrDefault();
        }

        private string[] getUniqueIds(XElement ns)
        {
            return ns.Elements(XmlNs.XFHIR + "uniqueId")
                     .Elements(XmlNs.XFHIR + "value")
                     .Attributes("value")
                     .Select(a => a.Value).ToArray<string>();
        }

        private string[] getCmSources(XElement cm)
        {
            return cm
                 .Elements(XmlNs.XFHIR + "sourceUri")
                 .Concat(cm
                    .Elements(XmlNs.XFHIR + "sourceReference")
                    .Elements(XmlNs.XFHIR + "reference"))
                 .Attributes("value").Select(a => a.Value)
                 .ToArray<string>();
        }

        private string[] getCmTargets(XElement cm)
        {
            return cm
                 .Elements(XmlNs.XFHIR + "targetUri")
                 .Concat(cm
                    .Elements(XmlNs.XFHIR + "targetReference")
                    .Elements(XmlNs.XFHIR + "reference"))
                 .Attributes("value").Select(a => a.Value)
                 .ToArray<string>();
        }

        private string getPrimitiveValueElement(XElement element, string name)
        {
            return element.Elements(XmlNs.XFHIR + name)
                    .Attributes("value")
                    .Select(a => a.Value).SingleOrDefault();
        }

        private class FullUrlAnnotation
        {
            public string FullUrl { get; set; }
        }

        // Use a forward-only XmlReader to scan through a possibly huge bundled file,
        // and yield the feed entries, so only one entry is in memory at a time
        internal IEnumerable<XElement> StreamResources()
        {
            var reader = SerializationUtil.XmlReaderFromStream(_input);

            var root = getRootName(reader);

            if (root == "Bundle")
            {
                if (!reader.ReadToDescendant("entry", XmlNs.FHIR)) yield break;

                while (!reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element
                        && reader.NamespaceURI == XmlNs.FHIR && reader.LocalName == "entry")
                    {
                        var entryNode = (XElement)XElement.ReadFrom(reader);
                        var fullUrl = entryNode.Elements(XmlNs.XFHIR + "fullUrl").Attributes("value").SingleOrDefault();
                        if (fullUrl != null)
                        {
                            var resourceNode = entryNode.Element(XName.Get("resource", XmlNs.FHIR));
                            if (resourceNode != null)
                            {
                                var resource = resourceNode.Elements().First();
                                resource.AddAnnotation(new FullUrlAnnotation { FullUrl = fullUrl.Value });
                                yield return resource;
                            }
                        }
                    }
                    else
                        reader.Read();
                }
            }

#if true
            // [WMR 20160908] Originally commented out logic
            //else if (root != null)
            //{
            //    var resourceNode = (XElement)XElement.ReadFrom(reader);
            //    var resourceId = resourceNode.Elements(XmlNs.XFHIR + "id").Attributes("value").SingleOrDefault();
            //    if (resourceId != null)
            //    {
            //        var fullUrl = resourceNode.Name.LocalName + "/" + resourceId.Value;
            //        resourceNode.Add(new XAttribute("scannerUrl", fullUrl));
            //        yield return resourceNode;
            //    }
            //}

            // [WMR 20160908] Fixed, parse stand-alone (conformance) resources
            else if (root != null)
            {
                var resourceNode = (XElement)XElement.ReadFrom(reader);
                // First try to initialize from canonical url (conformance resources)
                var canonicalUrl = resourceNode.Elements(XmlNs.XFHIR + "url").Attributes("value").SingleOrDefault();
                if (canonicalUrl != null)
                {
                    resourceNode.AddAnnotation(new FullUrlAnnotation { FullUrl = canonicalUrl.Value });
                    yield return resourceNode;
                }
                else
                {
                    // Otherwise try to initialize from resource id
                    var resourceId = resourceNode.Elements(XmlNs.XFHIR + "id").Attributes("value").SingleOrDefault();
                    if (resourceId != null)
                    {
                        var fullUrl = resourceNode.Name.LocalName + "/" + resourceId.Value;
                        resourceNode.AddAnnotation(new FullUrlAnnotation { FullUrl = fullUrl });
                        yield return resourceNode;
                    }
                }
            }
#endif

            else
                yield break;
        }


        private static string getRootName(XmlReader reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == XmlNs.FHIR)
                    return reader.LocalName;
            }

            return null;
        }


        public XElement FindResourceByUri(string uri)
        {
            if (uri == null) throw Error.ArgumentNull("uri");

            var resources = StreamResources();

            return resources.Where(res => fullUrl(res) == uri).SingleOrDefault();
        }

        private static string fullUrl(XElement resourceElement)
        {
            var ann = resourceElement.Annotation<FullUrlAnnotation>();

            if (ann != null)
            {
                return ann.FullUrl;
            }
            else
                return null;
        }


        internal class ResourceScanInformation
        {
            public ResourceType ResourceType { get; set; }

            public string ResourceUri { get; set; }

            public string Canonical { get; set; }

            public string ValueSetSystem { get; set; }

            public string[] UniqueIds { get; set; }

            public string[] ConceptMapSource { get; set; }

            public string[] ConceptMapTarget { get; set; }

            public string Origin { get; set; }

            public override string ToString()
            {
                return "{0} resource with uri {1} (canonical {2}), read from {2}"
                    .FormatWith(ResourceType, ResourceUri ?? "(unknown)", Canonical ?? "(unknown)", Origin);
            }
        }
    }
}
