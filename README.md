ApiActionSelector
========

This library copies necessary code from ASP.NET Web API Source to make ApiControllerActionSelector flexible.
Unfortunately current implementation does not provide any extension point. 

This class (currently) exposes static property `AmbiguousActionResolver`. This property can be used in case of resolving an ambiguous case.

For example, in this case we pick the first action (just to illustrate):

``` Csharp

ApiActionSelector.AmbiguousActionResolver = (context, actions) => actions.First();
```

If you cannot resolve, return null. This will result in the framework to throw the familiar ambiguous action exception.


####NOTE

I will be using this to resolve actions based on headers.
