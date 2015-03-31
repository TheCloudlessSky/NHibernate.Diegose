using System.Collections.Generic;

namespace NHibernate.CollectionQuery.Test.Domain
{
    public class Foo
    {
        public virtual int Id { get; set; }
        public virtual ICollection<Bar> Bars { get; set; } 
    }
}