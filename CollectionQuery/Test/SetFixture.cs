using System.Collections.Generic;
using NHibernate.CollectionQuery.Test.Domain;
using NHibernate.Mapping.ByCode;

namespace NHibernate.CollectionQuery.Test
{
    public class SetFixture : QueryableCollectionFixture
    {
        protected override ICollection<Bar> CreateCollection()
        {
            return new HashSet<Bar>();
        }

        protected override void CustomizeFooMapper(IClassMapper<Foo> mapper)
        {
            mapper.Set(x => x.Bars, spm =>
            {
                spm.Cascade(Cascade.All);
                spm.Type<PersistentQueryableSetType<Bar>>();
            },
            cer => cer.ManyToMany());
        }
    }

    public class SetOneToManyFixture : QueryableCollectionSimpleOneToManyFixture
    {
        protected override ICollection<Bar> CreateCollection()
        {
            return new HashSet<Bar>();
        }

        protected override void CustomizeFooMapper(IClassMapper<Foo> mapper)
        {
            mapper.Set(x => x.Bars, spm =>
            {
                spm.Cascade(Cascade.All);
                spm.Type<PersistentQueryableSetType<Bar>>();
            },
            cer => cer.OneToMany());
        }
    }
}