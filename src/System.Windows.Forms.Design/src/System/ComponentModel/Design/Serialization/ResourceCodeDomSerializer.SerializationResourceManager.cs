﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CodeDom;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Resources;
using System.Runtime.Serialization;

namespace System.ComponentModel.Design.Serialization
{
    internal partial class ResourceCodeDomSerializer
    {
        /// <summary>
        ///  This is the meat of resource serialization.  This implements a resource manager through a host-provided IResourceService interface.  The resource service feeds us with resource readers and writers, and we simulate a runtime ResourceManager. There is one instance of this object for the entire serialization process, just like there is one resource manager in runtime code.  When an instance of this object is created, it adds itself to the serialization manager's service list, and listens for the SerializationComplete event.  When serialization is complete, this will close and flush any readers or writers it may have opened and will also remove itself from the service list.
        /// </summary>
        internal class SerializationResourceManager : ComponentResourceManager
        {
            private static readonly object s_resourceSetSentinel = new object();
            private readonly IDesignerSerializationManager _manager;
            private bool _checkedLocalizationLanguage;
            private CultureInfo _localizationLanguage;
            private IResourceWriter _writer;
            private CultureInfo _readCulture;
            private readonly Hashtable _nameTable;
            private Hashtable _resourceSets;
            private Hashtable _metadata;
            private Hashtable _mergedMetadata;
            private object _rootComponent;
            private Hashtable _propertyFillAdded;
            private bool _invariantCultureResourcesDirty;
            private bool _metadataResourcesDirty;

            public SerializationResourceManager(IDesignerSerializationManager manager)
            {
                _manager = manager;
                _nameTable = new Hashtable();
                // We need to know when we're done so we can push the resource file out.
                manager.SerializationComplete += new EventHandler(OnSerializationComplete);
            }

            /// <summary>
            ///  State the serializers use to determine if the declaration of this resource manager has been performed.  This is just per-document state we keep; we do not actually care about this value.
            /// </summary>
            public bool DeclarationAdded { get; set; }

            /// <summary>
            ///  When a declaration is added, we also setup an expression other serializers can use to reference our resource declaration.  This bit tracks if we have setup this expression yet.  Note that the expression and declaration may be added at different times, if the declaration was added by a cached component.
            /// </summary>
            public bool ExpressionAdded { get; set; }

            /// <summary>
            ///  The language we should be localizing into.
            /// </summary>
            private CultureInfo LocalizationLanguage
            {
                get
                {
                    if (!_checkedLocalizationLanguage)
                    {
                        // Check to see if our base component's localizable prop is true
                        if (_manager.Context[typeof(RootContext)] is RootContext rootCtx)
                        {
                            object comp = rootCtx.Value;
                            PropertyDescriptor prop = TypeDescriptor.GetProperties(comp)["LoadLanguage"];
                            if (prop is not null && prop.PropertyType == typeof(CultureInfo))
                            {
                                _localizationLanguage = (CultureInfo)prop.GetValue(comp);
                            }
                        }

                        _checkedLocalizationLanguage = true;
                    }

                    return _localizationLanguage;
                }
            }

            /// <summary>
            ///  This is the culture info we should use to read and write resources. We always write using the same culture we read with so we don't stomp on data.
            /// </summary>
            private CultureInfo ReadCulture
            {
                get
                {
                    if (_readCulture is null)
                    {
                        CultureInfo locCulture = LocalizationLanguage;
                        if (locCulture is not null)
                        {
                            _readCulture = locCulture;
                        }
                        else
                        {
                            _readCulture = CultureInfo.InvariantCulture;
                        }
                    }

                    return _readCulture;
                }
            }

            /// <summary>
            ///  Returns a hash table where we shove resource sets.
            /// </summary>
            private Hashtable ResourceTable
            {
                get
                {
                    _resourceSets ??= new Hashtable();

                    return _resourceSets;
                }
            }

            /// <summary>
            ///  Retrieves the root component we're designing.
            /// </summary>
            private object RootComponent
            {
                get
                {
                    if (_rootComponent is null)
                    {
                        if (_manager.Context[typeof(RootContext)] is RootContext rootCtx)
                        {
                            _rootComponent = rootCtx.Value;
                        }
                    }

                    return _rootComponent;
                }
            }

            /// <summary>
            ///  Retrieves a resource writer we should write into.
            /// </summary>
            private IResourceWriter Writer
            {
                get
                {
                    if (_writer is null)
                    {
                        IResourceService rs = (IResourceService)_manager.GetService(typeof(IResourceService));

                        if (rs is not null)
                        {
                            // We always write with the culture we read with.  In the event of a language change during localization, we will write the new language to the source code and then perform a reload.
                            _writer = rs.GetResourceWriter(ReadCulture);
                        }
                        else
                        {
                            // No resource service, so there is no way to create a resource writer for the object.  In this case we just create an empty one so the resources go into the bit-bucket.
                            Debug.Fail("We expected to get IResourceService -- no resource serialization will be available");
                            _writer = new ResourceWriter(new MemoryStream());
                        }
                    }

                    return _writer;
                }
            }

            /// <summary>
            ///  The component serializer supports caching serialized outputs for speed.  It holds both a collection of statements as well as an opaque blob for resources.  This function adds data to that blob. The parameters to this function are the same as those to SetValue, or SetMetadata (when isMetadata is true).
            /// </summary>
            private static void AddCacheEntry(IDesignerSerializationManager manager, string name, object value, bool isMetadata, bool forceInvariant, bool shouldSerializeValue, bool ensureInvariant)
            {
                if (manager.Context[typeof(ComponentCache.Entry)] is ComponentCache.Entry entry)
                {
                    ComponentCache.ResourceEntry re = new ComponentCache.ResourceEntry
                    {
                        Name = name,
                        Value = value,
                        ForceInvariant = forceInvariant,
                        ShouldSerializeValue = shouldSerializeValue,
                        EnsureInvariant = ensureInvariant,
                        PropertyDescriptor = (PropertyDescriptor)manager.Context[typeof(PropertyDescriptor)],
                        ExpressionContext = (ExpressionContext)manager.Context[typeof(ExpressionContext)]
                    };

                    if (isMetadata)
                    {
                        entry.AddMetadata(re);
                    }
                    else
                    {
                        entry.AddResource(re);
                    }
                }
            }

            /// <summary>
            ///  Returns true if the caller should add a property fill statement for the given object.  A property fill is required for the component only once, so this remembers the value.
            /// </summary>
            public bool AddPropertyFill(object value)
            {
                bool added = false;
                if (_propertyFillAdded is null)
                {
                    _propertyFillAdded = new Hashtable();
                }
                else
                {
                    added = _propertyFillAdded.ContainsKey(value);
                }

                if (!added)
                {
                    _propertyFillAdded[value] = value;
                }

                return !added;
            }

            /// <summary>
            ///  This method examines all the resources for the provided culture. When it finds a resource with a key in the format of  &quot;[objectName].[property name]&quot; it will apply that resources value to the corresponding property on the object.
            /// </summary>
            public override void ApplyResources(object value, string objectName, CultureInfo culture)
            {
                culture ??= ReadCulture;

                // .NET Framework 4.0 (Dev10 #425129): Control location moves due to incorrect anchor info when resource files are reloaded.
                Windows.Forms.Control control = value as Windows.Forms.Control;
                control?.SuspendLayout();

                base.ApplyResources(value, objectName, culture);

                control?.ResumeLayout(false);
            }

            /// <summary>
            ///  This determines if the given resource name/value pair can be retrieved from a parent culture.  We don't want to write duplicate resources for each language, so we do a check of the parent culture.
            /// </summary>
            private CompareValue CompareWithParentValue(string name, object value)
            {
                Debug.Assert(name is not null, "name is null");
                // If there is no parent culture, treat that as being different from the parent's resource, which results in the "normal" code path for the caller.
                if (ReadCulture.Equals(CultureInfo.InvariantCulture))
                {
                    return CompareValue.Different;
                }

                CultureInfo culture = ReadCulture;
#pragma warning disable SA1009 // Closing parenthesis should be spaced correctly
                for (; ; )
#pragma warning restore SA1009 // Closing parenthesis should be spaced correctly
                {
                    Debug.Assert(culture.Parent != culture, "should have exited loop when culture = InvariantCulture");
                    culture = culture.Parent;
                    Hashtable rs = GetResourceSet(culture);
                    bool contains = (rs is null) ? false : rs.ContainsKey(name);
                    if (contains)
                    {
                        object parentValue = rs?[name];
                        if (parentValue == value)
                        {
                            return CompareValue.Same;
                        }
                        else if (parentValue is not null)
                        {
                            if (parentValue.Equals(value))
                            {
                                return CompareValue.Same;
                            }
                            else
                            {
                                return CompareValue.Different;
                            }
                        }
                        else
                        {
                            return CompareValue.Different;
                        }
                    }
                    else if (culture.Equals(CultureInfo.InvariantCulture))
                    {
                        return CompareValue.New;
                    }
                }
            }

            /// <summary>
            ///  Creates a resource set hashtable for the given resource reader.
            /// </summary>
            private Hashtable CreateResourceSet(IResourceReader reader, CultureInfo culture)
            {
                Hashtable result = new Hashtable();

                // We need to guard against bad or unloadable resources.  We warn the user in the task list here, but we will still load the designer.
                try
                {
                    IDictionaryEnumerator resEnum = reader.GetEnumerator();
                    while (resEnum.MoveNext())
                    {
                        string name = (string)resEnum.Key;
                        object value = resEnum.Value;
                        result[name] = value;
                    }
                }
                catch (Exception e)
                {
                    string message = e.Message;
                    if (message is null || message.Length == 0)
                    {
                        message = e.GetType().Name;
                    }

                    Exception se;

                    if (culture == CultureInfo.InvariantCulture)
                    {
                        se = new SerializationException(

                            string.Format(SR.SerializerResourceExceptionInvariant, message), e);
                    }
                    else
                    {
                        se = new SerializationException(string.Format(SR.SerializerResourceException, culture.ToString(), message), e);
                    }

                    _manager.ReportError(se);
                }

                return result;
            }

            /// <summary>
            ///  This returns a dictionary enumerator for metadata on the invariant culture.  If no metadata  can be found this will return null..
            /// </summary>
            public IDictionaryEnumerator GetMetadataEnumerator()
            {
                if (_mergedMetadata is null)
                {
                    Hashtable t = GetMetadata();
                    if (t is not null)
                    {
                        // This is for backwards compatibility and also for the case when our reader/writer don't support metadata.  We must merge the original enumeration data in here or  else existing design time properties won't show up.  That would be really bad for things like Localizable.
                        Hashtable it = GetResourceSet(CultureInfo.InvariantCulture);
                        if (it is not null)
                        {
                            foreach (DictionaryEntry de in it)
                            {
                                if (!t.ContainsKey(de.Key))
                                {
                                    t.Add(de.Key, de.Value);
                                }
                            }
                        }

                        _mergedMetadata = t;
                    }
                }

                if (_mergedMetadata is not null)
                {
                    return _mergedMetadata.GetEnumerator();
                }

                return null;
            }

            /// <summary>
            ///  This returns a dictionary enumerator for the given culture.  If no such resource file exists for the culture this  will return null.
            /// </summary>
            public IDictionaryEnumerator GetEnumerator(CultureInfo culture)
            {
                Hashtable ht = GetResourceSet(culture);
                if (ht is not null)
                {
                    return ht.GetEnumerator();
                }

                return null;
            }

            /// <summary>
            ///  Loads the metadata table
            /// </summary>
            private Hashtable GetMetadata()
            {
                if (_metadata is null)
                {
                    IResourceService resSvc = (IResourceService)_manager.GetService(typeof(IResourceService));
                    if (resSvc is not null)
                    {
                        IResourceReader reader = resSvc.GetResourceReader(CultureInfo.InvariantCulture);
                        if (reader is not null)
                        {
                            try
                            {
                                if (reader is ResXResourceReader resxReader)
                                {
                                    _metadata = new Hashtable();
                                    IDictionaryEnumerator de = resxReader.GetMetadataEnumerator();
                                    while (de.MoveNext())
                                    {
                                        _metadata[de.Key] = de.Value;
                                    }
                                }
                            }
                            finally
                            {
                                reader.Close();
                            }
                        }
                    }
                }

                return _metadata;
            }

            /// <summary>
            ///  Overrides ResourceManager.GetObject to return the requested object.  Returns null if the object couldn't be found.
            /// </summary>
            public override object GetObject(string resourceName)
            {
                return GetObject(resourceName, false);
            }

            /// <summary>
            ///  Retrieves the object of the given name from our resource bundle. If forceInvariant is true, this will always use the invariant resource, rather than using the current language. Returns null if the object couldn't be found.
            /// </summary>
            public object GetObject(string resourceName, bool forceInvariant)
            {
                Debug.Assert(_manager is not null, "This resource manager object has been destroyed.");
                // We fetch the read culture if someone asks for a culture-sensitive string.  If forceInvariant is set, we always use the invariant culture.
                CultureInfo culture;

                if (forceInvariant)
                {
                    culture = CultureInfo.InvariantCulture;
                }
                else
                {
                    culture = ReadCulture;
                }

                object value = null;
                while (value is null)
                {
                    Hashtable rs = GetResourceSet(culture);
                    if (rs is not null)
                    {
                        value = rs[resourceName];
                    }

                    CultureInfo lastCulture = culture;
                    culture = culture.Parent;
                    if (lastCulture.Equals(culture))
                    {
                        break;
                    }
                }

                return value;
            }

            /// <summary>
            ///  Looks up the resource set in the resourceSets hash table, loading the set if it hasn't been loaded already. Returns null if no resource that exists for that culture.
            /// </summary>
            private Hashtable GetResourceSet(CultureInfo culture)
            {
                Debug.Assert(culture is not null, "null parameter");
                Hashtable rs = null;
                object objRs = ResourceTable[culture];
                if (objRs is null)
                {
                    IResourceService resSvc = (IResourceService)_manager.GetService(typeof(IResourceService));
                    TraceErrorIf(resSvc is null, "IResourceService is not available.  We will not be able to load resources.");
                    if (resSvc is not null)
                    {
                        IResourceReader reader = resSvc.GetResourceReader(culture);
                        if (reader is not null)
                        {
                            try
                            {
                                rs = CreateResourceSet(reader, culture);
                            }
                            finally
                            {
                                reader.Close();
                            }

                            ResourceTable[culture] = rs;
                        }
                        else
                        {
                            // Provide a sentinel so we don't repeatedly ask for the same resource.  If this is the invariant culture, always provide one.
                            if (culture.Equals(CultureInfo.InvariantCulture))
                            {
                                rs = new Hashtable();
                                ResourceTable[culture] = rs;
                            }
                            else
                            {
                                ResourceTable[culture] = s_resourceSetSentinel;
                            }
                        }
                    }
                }
                else
                {
                    rs = objRs as Hashtable;
                    if (rs is null)
                    {
                        // the resourceSets hash table may contain our "this" pointer as a sentinel value
                        Debug.Assert(objRs == s_resourceSetSentinel, "unknown object in resourceSets: " + objRs);
                    }
                }

                return rs;
            }

            /// <summary>
            ///  Override of GetResourceSet from ResourceManager.
            /// </summary>
            public override ResourceSet GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
            {
                ArgumentNullException.ThrowIfNull(culture);

                CultureInfo lastCulture;
                do
                {
                    lastCulture = culture;
                    culture = culture.Parent;
                }
                while (tryParents && !lastCulture.Equals(culture));

                if (createIfNotExists)
                {
                    return new CodeDomResourceSet();
                }

                return null;
            }

            /// <summary>
            ///  Overrides ResourceManager.GetString to return the requested string. Returns null if the string couldn't be found.
            /// </summary>
            public override string GetString(string resourceName)
            {
                return GetObject(resourceName, false) as string;
            }

            /// <summary>
            ///  Event handler that gets called when serialization or deserialization is complete. Here we need to write any resources to disk.  Sine we open resources for write on demand, this code handles the case of reading resources as well.
            /// </summary>
            private void OnSerializationComplete(object sender, EventArgs e)
            {
                // Commit any changes we have made.
                if (_writer is not null)
                {
                    _writer.Close();
                    _writer = null;
                }

                if (_invariantCultureResourcesDirty || _metadataResourcesDirty)
                {
                    IResourceService service = (IResourceService)_manager.GetService(typeof(IResourceService));
                    if (service is not null)
                    {
                        IResourceWriter invariantWriter = service.GetResourceWriter(CultureInfo.InvariantCulture);
                        Debug.Assert(invariantWriter is not null, "GetResourceWriter returned null for the InvariantCulture");

                        try
                        {
                            // Do the invariant resources first
                            Debug.Assert(!ReadCulture.Equals(CultureInfo.InvariantCulture), "invariantCultureResourcesDirty should only come into play when readCulture != CultureInfo.InvariantCulture; check that CompareWithParentValue is correct");
                            object objRs = ResourceTable[CultureInfo.InvariantCulture];
                            Debug.Assert(objRs is not null && objRs is Hashtable, "ResourceSet for the InvariantCulture not loaded, but it's considered dirty?");
                            Hashtable resourceSet = (Hashtable)objRs;

                            // Dump the hash table to the resource writer
                            IDictionaryEnumerator resEnum = resourceSet.GetEnumerator();
                            while (resEnum.MoveNext())
                            {
                                string name = (string)resEnum.Key;
                                object value = resEnum.Value;
                                invariantWriter.AddResource(name, value);
                            }

                            _invariantCultureResourcesDirty = false;

                            // Followed by the metadata.
                            Debug.Assert(_metadata is not null, "No metadata, but it's dirty?");
                            if (invariantWriter is ResXResourceWriter resxWriter)
                            {
                                foreach (DictionaryEntry de in _metadata)
                                {
                                    resxWriter.AddMetadata((string)de.Key, de.Value);
                                }
                            }
                            else
                            {
                                Debug.Fail("Metadata not supported, but it's dirty?");
                            }

                            _metadataResourcesDirty = false;
                        }
                        finally
                        {
                            invariantWriter.Close();
                        }
                    }
                    else
                    {
                        Debug.Fail("Couldn't find IResourceService");
                        _invariantCultureResourcesDirty = false;
                        _metadataResourcesDirty = false;
                    }
                }
            }

            /// <summary>
            ///  Writes a metadata tag to the resource, or writes a normal tag if the resource writer doesn't support metadata.
            /// </summary>
            public void SetMetadata(IDesignerSerializationManager manager, string resourceName, object value, bool shouldSerializeValue, bool applyingCachedResources)
            {
                if (value is not null && (!value.GetType().IsSerializable))
                {
                    Debug.Fail("Cannot save a non-serializable value into resources.  Add serializable to " + (value is null ? "(null)" : value.GetType().Name));
                    return;
                }

                // If we are currently the invariant culture then we may be able to  write directly.
                if (ReadCulture.Equals(CultureInfo.InvariantCulture))
                {
                    if (shouldSerializeValue)
                    {
                        if (Writer is ResXResourceWriter resxWriter)
                        {
                            resxWriter.AddMetadata(resourceName, value);
                        }
                        else
                        {
                            Writer.AddResource(resourceName, value);
                        }
                    }
                }
                else
                {
                    // Check if the invariant writer supports metadata. If not, we need to push metadata as regular data.
                    IResourceWriter invariantWriter = null;
                    IResourceService service = (IResourceService)manager.GetService(typeof(IResourceService));
                    if (service is not null)
                    {
                        invariantWriter = service.GetResourceWriter(CultureInfo.InvariantCulture);
                    }

                    Hashtable invariant = GetResourceSet(CultureInfo.InvariantCulture);
                    Hashtable t;
                    if (invariantWriter is null || invariantWriter is ResXResourceWriter)
                    {
                        t = GetMetadata();
                        if (t is null)
                        {
                            _metadata = new Hashtable();
                            t = _metadata;
                        }

                        // Note that when we read metadata, for backwards compatibility, we also merge in regular data from the invariant resource. We need to clear that data here, since we are going to write out metadata separately.
                        if (invariant.ContainsKey(resourceName))
                        {
                            invariant.Remove(resourceName);
                        }

                        _metadataResourcesDirty = true;
                    }
                    else
                    {
                        t = invariant;
                        _invariantCultureResourcesDirty = true;
                    }

                    Debug.Assert(t is not null, "Don't know where to push metadata.");
                    if (t is not null)
                    {
                        if (shouldSerializeValue)
                        {
                            t[resourceName] = value;
                        }
                        else
                        {
                            t.Remove(resourceName);
                        }
                    }

                    _mergedMetadata = null;
                }

                // Update the component cache, if we have one active
                if (!applyingCachedResources)
                {
                    AddCacheEntry(manager, resourceName, value, true, false, shouldSerializeValue, false);
                }
            }

            /// <summary>
            ///  Writes the given resource value under the given name. This checks the parent resource to see if the values are the same.  If they are, the resource is not written.  If not, then the resource is written. We always write using the resource language we read in with, so we don't stomp on the wrong resource data in the event that someone changes the language.
            /// </summary>
            public void SetValue(IDesignerSerializationManager manager, string resourceName, object value, bool forceInvariant, bool shouldSerializeInvariant, bool ensureInvariant, bool applyingCachedResources)
            {
                // Values we are going to serialize must be serializable or else the resource writer will fail when we close it.
                if (value is not null && (!value.GetType().IsSerializable))
                {
                    Debug.Fail("Cannot save a non-serializable value into resources.  Add serializable to " + (value is null ? "(null)" : value.GetType().Name));
                    return;
                }

                if (forceInvariant)
                {
                    if (ReadCulture.Equals(CultureInfo.InvariantCulture))
                    {
                        if (shouldSerializeInvariant)
                        {
                            Writer.AddResource(resourceName, value);
                        }
                    }
                    else
                    {
                        Hashtable resourceSet = GetResourceSet(CultureInfo.InvariantCulture);
                        Debug.Assert(resourceSet is not null, "No ResourceSet for the InvariantCulture?");
                        if (shouldSerializeInvariant)
                        {
                            resourceSet[resourceName] = value;
                        }
                        else
                        {
                            resourceSet.Remove(resourceName);
                        }

                        _invariantCultureResourcesDirty = true;
                    }
                }
                else
                {
                    CompareValue comparison = CompareWithParentValue(resourceName, value);
                    switch (comparison)
                    {
                        case CompareValue.Same:
                            // don't add to any resource set
                            break;
                        case CompareValue.Different:
                            Writer.AddResource(resourceName, value);
                            break;
                        case CompareValue.New:
                            if (ensureInvariant)
                            {
                                // Add resource to InvariantCulture
                                Debug.Assert(!ReadCulture.Equals(CultureInfo.InvariantCulture), "invariantCultureResourcesDirty should only come into play when readCulture != CultureInfo.InvariantCulture; check that CompareWithParentValue is correct");
                                Hashtable resourceSet = GetResourceSet(CultureInfo.InvariantCulture);
                                Debug.Assert(resourceSet is not null, "No ResourceSet for the InvariantCulture?");
                                resourceSet[resourceName] = value;
                                _invariantCultureResourcesDirty = true;
                                Writer.AddResource(resourceName, value);
                            }
                            else
                            {
                                // This is a new value.  We want to write it out, PROVIDED that the value is not associated with a property that is currently returning false from ShouldSerializeValue.  This allows us to skip writing out Font == NULL on all non-invariant cultures, but still allow us to write out the value if the user is resetting a font back to null. If we cannot associate the value with a property we will write it out just to be safe.
                                // In addition, we need to handle the case of the user adding a new component to the non-invariant language.  This would be bad, because when he/she moved back to the invariant language the component's properties would all be defaults.  In order to minimize this problem, but still allow holes in the invariant resx, we also check to see if the property can be reset.  If it cannot be reset, that means that it has no meaningful default. Therefore, it should have appeared in the invariant resx and its absence indicates a new component.
                                bool writeValue = true;
                                bool writeInvariant = false;
                                PropertyDescriptor prop = (PropertyDescriptor)manager.Context[typeof(PropertyDescriptor)];
                                if (prop is not null)
                                {
                                    ExpressionContext tree = (ExpressionContext)manager.Context[typeof(ExpressionContext)];
                                    if (tree is not null && tree.Expression is CodePropertyReferenceExpression)
                                    {
                                        writeValue = prop.ShouldSerializeValue(tree.Owner);
                                        writeInvariant = !prop.CanResetValue(tree.Owner);
                                    }
                                }

                                if (writeValue)
                                {
                                    Writer.AddResource(resourceName, value);
                                    if (writeInvariant)
                                    {
                                        // Add resource to InvariantCulture
                                        Debug.Assert(!ReadCulture.Equals(CultureInfo.InvariantCulture), "invariantCultureResourcesDirty should only come into play when readCulture != CultureInfo.InvariantCulture; check that CompareWithParentValue is correct");
                                        Hashtable resourceSet = GetResourceSet(CultureInfo.InvariantCulture);
                                        Debug.Assert(resourceSet is not null, "No ResourceSet for the InvariantCulture?");
                                        resourceSet[resourceName] = value;
                                        _invariantCultureResourcesDirty = true;
                                    }
                                }
                            }

                            break;
                        default:
                            Debug.Fail("Unknown CompareValue " + comparison);
                            break;
                    }
                }

                // Update the component cache, if we have one active.  We don't have to be fancy here because updating this cache just indicates that code in the component cache will later call us to re-apply the resources, and our logic above will be called again.
                if (!applyingCachedResources)
                {
                    AddCacheEntry(manager, resourceName, value, false, forceInvariant, shouldSerializeInvariant, ensureInvariant);
                }
            }

            /// <summary>
            ///  Writes the given resource value under the given name. This checks the parent resource to see if the values are the same.  If they are, the resource is not written.  If not, then the resource is written.  We always write using the resource language we read in with, so we don't stomp on the wrong resource data in the event that someone changes the language.
            /// </summary>
            public string SetValue(IDesignerSerializationManager manager, ExpressionContext tree, object value, bool forceInvariant, bool shouldSerializeInvariant, bool ensureInvariant, bool applyingCachedResources)
            {
                string nameBase;
                bool appendCount = false;
                if (tree is not null)
                {
                    if (tree.Owner == RootComponent)
                    {
                        nameBase = "$this";
                    }
                    else
                    {
                        nameBase = manager.GetName(tree.Owner);
                        if (nameBase is null)
                        {
                            IReferenceService referenceService = (IReferenceService)manager.GetService(typeof(IReferenceService));
                            if (referenceService is not null)
                            {
                                nameBase = referenceService.GetName(tree.Owner);
                            }
                        }
                    }

                    CodeExpression expression = tree.Expression;
                    string expressionName;
                    if (expression is CodePropertyReferenceExpression)
                    {
                        expressionName = ((CodePropertyReferenceExpression)expression).PropertyName;
                    }
                    else if (expression is CodeFieldReferenceExpression)
                    {
                        expressionName = ((CodeFieldReferenceExpression)expression).FieldName;
                    }
                    else if (expression is CodeMethodReferenceExpression)
                    {
                        expressionName = ((CodeMethodReferenceExpression)expression).MethodName;
                        if (expressionName.StartsWith("Set"))
                        {
                            expressionName = expressionName.Substring(3);
                        }
                    }
                    else
                    {
                        expressionName = null;
                    }

                    nameBase ??= "resource";

                    if (expressionName is not null)
                    {
                        nameBase += "." + expressionName;
                    }
                }
                else
                {
                    nameBase = "resource";
                    appendCount = true;
                }

                // Now find an unused name
                string resourceName = nameBase;
                int count = 1;

#pragma warning disable SA1009 // Closing parenthesis should be spaced correctly
                for (; ; )
#pragma warning restore SA1009 // Closing parenthesis should be spaced correctly
                {
                    if (appendCount)
                    {
                        resourceName = nameBase + count.ToString(CultureInfo.InvariantCulture);
                        count++;
                    }
                    else
                    {
                        appendCount = true;
                    }

                    if (!_nameTable.ContainsKey(resourceName))
                    {
                        break;
                    }
                }

                // Now that we have a name, write out the resource.
                SetValue(manager, resourceName, value, forceInvariant, shouldSerializeInvariant, ensureInvariant, applyingCachedResources);
                _nameTable[resourceName] = resourceName;
                return resourceName;
            }

            private class CodeDomResourceSet : ResourceSet
            {
                public CodeDomResourceSet()
                {
                }
            }

            private enum CompareValue
            {
                Same, // parent value == child value
                Different, // parent value exists, but != child value
                New, // parent value does not exist
            }
        }
    }
}