﻿using Hl7.ElementModel;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Navigation;
using Hl7.Fhir.Specification.Snapshot;
using Hl7.Fhir.Specification.Source;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Hl7.Fhir.Validation
{
    [Trait("Category", "Validation")]
    public class SliceValidationTests : IClassFixture<ValidationFixture>
    {
        private IResourceResolver _resolver;
        private Validator _validator;

        public SliceValidationTests(ValidationFixture fixture)
        {
            _resolver = fixture.Resolver;
            _validator = fixture.Validator;
        }


        private IBucket createSliceDefs()
        {
            var sd = _resolver.FindStructureDefinition("http://example.com/StructureDefinition/patient-telecom-reslice-ek");
            Assert.NotNull(sd);
            var snapgen = new SnapshotGenerator(_resolver);
            snapgen.Update(sd);

            // sd.Snapshot.Element.Where(e => e.Path.EndsWith(".telecom")).Select(e=>e.Path + " : " + e.Name ?? "").ToArray()

            var nav = new ElementDefinitionNavigator(sd);
            var success = nav.JumpToFirst("Patient.telecom");
            Assert.True(success);
            //var xml = FhirSerializer.SerializeResourceToXml(sd);
            //File.WriteAllText(@"c:\temp\sdout.xml", xml);

            return BucketFactory.CreateRoot(nav, _validator);
        }

        /*
         *  telecom [1..4]/openatend/ordered
	     *      telecom:phone [1..2]
	     *      telecom:email [0..1]/closed
		 *          telecom:email/home [0..*]
		 *          telecom:email/work [0..*]
         *  	telecom:other [0..3]/open
		 *          telecom:other/home [0..1]
		 *          telecom:other/work [0..1]
	    */

        [Fact]
        public void TestSliceSetup()
        {
            var s = createSliceDefs();
            Assert.IsType<SliceGroupBucket>(s);
            var slice = s as SliceGroupBucket;

            Assert.Equal(ElementDefinition.SlicingRules.OpenAtEnd, slice.Rules);
            Assert.Equal(true, slice.Ordered);
            Assert.Equal("Patient.telecom", slice.Name);
            Assert.Equal(3, slice.ChildSlices.Count);
            Assert.IsType<ElementBucket>(slice.Entry);

            Assert.IsType<SliceBucket>(slice.ChildSlices[0]);
            Assert.Equal("Patient.telecom:phone", slice.ChildSlices[0].Name);

            Assert.IsType<SliceGroupBucket>(slice.ChildSlices[1]);
            var email = slice.ChildSlices[1] as SliceGroupBucket;
            Assert.Equal("Patient.telecom:email", email.Name);
            Assert.Equal(ElementDefinition.SlicingRules.Closed, email.Rules);
            Assert.Equal(false, email.Ordered);

            Assert.IsType<SliceBucket>(email.Entry);
            Assert.Equal(2, email.ChildSlices.Count);

            Assert.IsType<SliceBucket>(email.ChildSlices[0]);
            Assert.Equal("Patient.telecom:email/home", email.ChildSlices[0].Name);

            Assert.IsType<SliceBucket>(email.ChildSlices[1]);
            Assert.Equal("Patient.telecom:email/work", email.ChildSlices[1].Name);

            Assert.IsType<SliceGroupBucket>(slice.ChildSlices[2]);
            var other = slice.ChildSlices[2] as SliceGroupBucket;
            Assert.Equal("Patient.telecom:other", other.Name);
            Assert.Equal(ElementDefinition.SlicingRules.Open, other.Rules);
        }

        [Fact]
        public void TestDiscriminatedTelecomSliceUse()
        {
            var p = new Patient();

            // Incorrect "home" use for slice "phone"
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Phone, Use = ContactPoint.ContactPointUse.Home, Value = "e.kramer@furore.com" });

            // Incorrect use of "use" for slice "other"
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Use = ContactPoint.ContactPointUse.Home, Value = "http://nu.nl" });

            // Correct use of slice "other"
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Value = "http://nu.nl" });

            // Correct "work" use for slice "phone", but out of order
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Phone, Use = ContactPoint.ContactPointUse.Work, Value = "ewout@di.nl" });

            var outcome = _validator.Validate(p, "http://example.com/StructureDefinition/patient-telecom-slice-ek");
            Assert.False(outcome.Success);
            Assert.Equal(3, outcome.Errors);
            Assert.Equal(0, outcome.Warnings);
            var repr = outcome.ToString();
            Assert.Contains("matches slice 'Patient.telecom:phone', but this is out of order for group 'Patient.telecom'", repr);
            Assert.Contains("Value is not exactly equal to fixed value 'work'", repr);
            Assert.Contains("Instance count for 'Patient.telecom.use' is 1", repr);
        }

        [Fact]
        public void TestBucketAssignment()
        {
            var s = createSliceDefs() as SliceGroupBucket;

            var p = new Patient();
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Phone, Use = ContactPoint.ContactPointUse.Home, Value = "+31-6-39015765" });
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Email, Use = ContactPoint.ContactPointUse.Work, Value = "e.kramer@furore.com" });
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Use = ContactPoint.ContactPointUse.Temp, Value = "skype://crap" });
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Use = ContactPoint.ContactPointUse.Home, Value = "http://nu.nl" });
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Fax, Use = ContactPoint.ContactPointUse.Work, Value = "+31-20-6707070" });
            var pnav = new PocoNavigator(p) as IElementNavigator;

            var telecoms = pnav.GetChildrenByName("telecom");

            foreach(var telecom in telecoms)
                Assert.True(s.Add(telecom));

            var outcome = s.Validate(_validator, pnav);
            Assert.True(outcome.Success);
            Assert.Equal(0, outcome.Warnings);
            
            Assert.Equal("+31-6-39015765", s.ChildSlices[0].Members.Single().Values("value").Single());

            var emailBucket = s.ChildSlices[1] as SliceGroupBucket;
            Assert.Equal("e.kramer@furore.com", emailBucket.Members.Single().Values("value").Single());
            Assert.False(emailBucket.ChildSlices[0].Members.Any());
            Assert.Equal("e.kramer@furore.com", emailBucket.ChildSlices[1].Members.Single().Values("value").Single());
           
            var otherBucket = s.ChildSlices[2] as SliceGroupBucket;
            Assert.Equal("http://nu.nl", otherBucket.ChildSlices[0].Members.Single().Values("value").Single());
            Assert.False(otherBucket.ChildSlices[1].Members.Any());
            Assert.Equal("skype://crap", otherBucket.Members.First().Values("value").Single()); // in the open slice - find it on other bucket, not child

            Assert.Equal("+31-20-6707070", s.Members.Last().Values("value").Single()); // in the open-at-end slice
        }

        [Fact]
        public void TestTelecomReslicing()
        {
            var p = new Patient();

            // Incorrect "old" use for closed slice telecom:email
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Email, Use = ContactPoint.ContactPointUse.Home, Value = "e.kramer@furore.com" });
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Email, Use = ContactPoint.ContactPointUse.Old, Value = "ewout@di.nl" });

            // Too many for telecom:other/home
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Use = ContactPoint.ContactPointUse.Home, Value = "http://nu.nl" });
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Use = ContactPoint.ContactPointUse.Home, Value = "http://nos.nl" });

            // Out of order openAtEnd
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Fax, Use = ContactPoint.ContactPointUse.Work, Value = "+31-20-6707070" });

            // For the open slice in telecom:other
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Other, Use = ContactPoint.ContactPointUse.Temp, Value = "skype://crap" });  // open slice

            // Out of order (already have telecom:other)
            p.Telecom.Add(new ContactPoint { System = ContactPoint.ContactPointSystem.Phone, Use = ContactPoint.ContactPointUse.Home, Value = "+31-6-39015765" });

            var outcome = _validator.Validate(p, "http://example.com/StructureDefinition/patient-telecom-reslice-ek");
            Assert.False(outcome.Success);
            Assert.Equal(7, outcome.Errors);
            Assert.Equal(0, outcome.Warnings);
            var repr = outcome.ToString();
            Assert.Contains("not within the specified cardinality of 1..5 (at Patient)", repr);
            Assert.Contains("which is not allowed for an open-at-end group (at Patient.telecom[5])", repr);
            Assert.Contains("a previous element already matched slice 'Patient.telecom:other' (at Patient.telecom[6])", repr);
            Assert.Contains("group at 'Patient.telecom:email' is closed. (at Patient.telecom[1])", repr);
        }
    }
}
