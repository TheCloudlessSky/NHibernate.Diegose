﻿using System.Collections.Generic;
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

        protected override void CustomizeMapping(ConventionModelMapper mapper)
        {
            mapper.Class<Foo>(cm => cm.Set(x => x.Bars, spm =>
                                                        {
                                                            spm.Cascade(Cascade.All);
                                                            spm.Type<PersistentQueryableSetType<Bar>>();
                                                        }));
        }
    }
}