using System.Collections.Generic;
using NHibernate.CollectionQuery.Test.Domain;
using NHibernate.Mapping.ByCode;

namespace NHibernate.CollectionQuery.Test
{
    public class BagFixture : QueryableCollectionFixture
    {
        protected override ICollection<Bar> CreateCollection()
        {
            return new List<Bar>();
        }

        protected override void CustomizeFooMapper(IClassMapper<Foo> mapper)
        {
            mapper.Bag(x => x.Bars, bpm =>
            {
                bpm.Cascade(Cascade.All);
                bpm.Type<PersistentQueryableBagType<Bar>>();
            },
            cer => cer.ManyToMany());
        }
    }

    public class BagOneToManyFixture : QueryableCollectionSimpleOneToManyFixture
    {
        protected override ICollection<Bar> CreateCollection()
        {
            return new List<Bar>();
        }

        protected override void CustomizeFooMapper(IClassMapper<Foo> mapper)
        {
            mapper.Bag(x => x.Bars, bpm =>
            {
                bpm.Cascade(Cascade.All);
                bpm.Type<PersistentQueryableBagType<Bar>>();
            },
            cer => cer.OneToMany());
        }
    }
}