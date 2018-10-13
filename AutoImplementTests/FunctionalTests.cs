﻿using System.ComponentModel;
using Xunit;

namespace AutoImplementTests {
   public class FunctionalTests {
      [Fact]
      public void CustomEventStubClassActuallyWorksAsExpect() {
         void HelperMethod(object sender, PropertyChangedEventArgs args) { }
         var implementation = new StubNotifyPropertyChanged();
         implementation.PropertyChanged += HelperMethod;
         implementation.PropertyChanged -= HelperMethod;
         Assert.Empty(implementation.PropertyChanged.handlers);
      }
   }
}
