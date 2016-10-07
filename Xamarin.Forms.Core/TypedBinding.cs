using System;
using System.ComponentModel;
using System.Globalization;
using System.Collections.Generic;

namespace Xamarin.Forms.Internals
{
	public sealed class TypedBinding<TSource,TProperty> : BindingBase
	{
		readonly IValueConverter _converter;
		readonly object _converterParameter;
		readonly Func<TSource, TProperty> _getter;
		readonly Action<TSource, TProperty> _setter;
		readonly WeakPropertyChangedProxy [] _handlers;
		readonly object _source;
		readonly string _updateSourceEventName;
		public delegate object PartGetter(TSource source);
		public TypedBinding(Func<TSource, TProperty> getter, Action<TSource, TProperty> setter, Tuple<PartGetter, string> [] handlers, BindingMode mode = BindingMode.Default, IValueConverter converter = null, object converterParameter = null, string stringFormat = null, object source = null, string updateSourceEventName = null)
		{
			if (getter == null)
				throw new ArgumentNullException(nameof(getter));
			
			_getter = getter;
			_setter = setter;
			Mode = mode;
			_converter = converter;
			_converterParameter = converterParameter;
			StringFormat = stringFormat;
			_source = source;
			_updateSourceEventName = updateSourceEventName;

			if (handlers == null)
				return;

			_handlers = new WeakPropertyChangedProxy [handlers.Length];
			for (var i = 0; i < handlers.Length; i++) _handlers [i] = new WeakPropertyChangedProxy { PartGetter = handlers [i].Item1, PropertyName = handlers [i].Item2 };
		}

		BindableProperty _targetProperty;
		WeakReference<object> _weakSource;
		WeakReference<BindableObject> _weakTarget;

		// Applies the binding to a previously set source and target.
		internal override void Apply(bool fromTarget = false)
		{
			base.Apply(fromTarget);
			if (_weakSource == null || _weakTarget == null)
				return;

			BindableObject target;
			if (!_weakTarget.TryGetTarget(out target)) {
				Unapply();
				return;
			}
			object source;
			if (_weakSource.TryGetTarget(out source) && _targetProperty != null)
				ApplyCore(source, target, _targetProperty, fromTarget);
		}

		// Applies the binding to a new source or target.
		internal override void Apply(object context, BindableObject bindObj, BindableProperty targetProperty)
		{
			object src = _source;
			base.Apply(src ?? context, bindObj, targetProperty);
			var source = src ?? Context ?? context;
			_targetProperty = targetProperty;
			BindableObject prevTarget;
			if (_weakTarget != null && _weakTarget.TryGetTarget(out prevTarget) && !ReferenceEquals(prevTarget, bindObj))
				throw new InvalidOperationException("Binding instances can not be reused");

			object previousSource;
			if (_weakSource != null && _weakSource.TryGetTarget(out previousSource) && !ReferenceEquals(previousSource, source))
				throw new InvalidOperationException("Binding instances can not be reused");

			_weakSource = new WeakReference<object>(source);
			_weakTarget = new WeakReference<BindableObject>(bindObj);

			ApplyCore(source, bindObj, targetProperty);
		}

		internal override BindingBase Clone()
		{
			Tuple<PartGetter, string> [] handlers = _handlers == null ? null : new Tuple<PartGetter, string> [_handlers.Length];
			if (handlers != null) {
				for (var i = 0; i < _handlers.Length; i++)
					handlers [i] = new Tuple<PartGetter, string>(_handlers [i].PartGetter, _handlers [i].PropertyName);
			}
			return new TypedBinding<TSource, TProperty>(_getter, _setter, handlers, Mode, _converter, _converterParameter, StringFormat, _source, _updateSourceEventName);
		}

		internal override object GetSourceValue(object value, Type targetPropertyType)
		{
			if (_converter != null)
				value = _converter.Convert(value, targetPropertyType, _converterParameter, CultureInfo.CurrentUICulture);

			return base.GetSourceValue(value, targetPropertyType);
		}

		internal override object GetTargetValue(object value, Type sourcePropertyType)
		{
			if (_converter != null)
				value = _converter.ConvertBack(value, sourcePropertyType, _converterParameter, CultureInfo.CurrentUICulture);

			return base.GetTargetValue(value, sourcePropertyType);
		}

		internal override void Unapply()
		{
			base.Unapply();
			Unsubscribe();
			_weakSource = null;
			_weakTarget = null;
		}

		void ApplyCore(object sourceObject, BindableObject target, BindableProperty property, bool fromTarget = false)
		{
			var mode = this.GetRealizedMode(_targetProperty);
			if (mode == BindingMode.OneWay && fromTarget)
				return;

			bool needsGetter = (mode == BindingMode.TwoWay && !fromTarget) || mode == BindingMode.OneWay;
			bool needsSetter = !needsGetter && ((mode == BindingMode.TwoWay && fromTarget) || mode == BindingMode.OneWayToSource);

			if (sourceObject != null && sourceObject is TSource && (mode == BindingMode.OneWay || mode == BindingMode.TwoWay)) Subscribe((TSource)sourceObject);

			if (needsGetter) {
				var value = property.DefaultValue;
				if (sourceObject != null && sourceObject is TSource) {
					try {
						value = GetSourceValue(_getter((TSource)sourceObject), property.ReturnType);
					} catch (Exception ex) when (ex is NullReferenceException || ex is KeyNotFoundException) {
					}
				}
				if (!TryConvert(ref value, property.ReturnType, true)) {
					Log.Warning("Binding", "{0} can not be converted to type '{1}'", value, property.ReturnType);
					return;
				}
				target.SetValueCore(property, value, BindableObject.SetValueFlags.ClearDynamicResource, BindableObject.SetValuePrivateFlags.Default | BindableObject.SetValuePrivateFlags.Converted);
			} else if (needsSetter && _setter != null && sourceObject != null && sourceObject is TSource) {
				var value = GetTargetValue(target.GetValue(property), typeof(TProperty));
				if (!TryConvert(ref value, typeof(TProperty), false)) {
					Log.Warning("Binding", "{0} can not be converted to type '{1}'", value, typeof(TProperty));
					return;
				}

				_setter((TSource)sourceObject, (TProperty)value);
			}
		}

		bool TryConvert(ref object value, Type convertTo, bool toTarget)
		{
			if (value == null)
				return true;
			if ((toTarget && _targetProperty.TryConvert(ref value)) || (!toTarget && convertTo.IsInstanceOfType(value)))
				return true;

			object original = value;
			try {
				value = Convert.ChangeType(value, convertTo, CultureInfo.InvariantCulture);
				return true;
			} catch (Exception ex ) when (ex is InvalidCastException || ex is FormatException||ex is OverflowException) {
				value = original;
				return false;
			}
		}

		struct WeakPropertyChangedProxy
		{
			public PartGetter PartGetter { get; set; }
			public string PropertyName { get; set; }
			public WeakReference Part { get; set; }
			public BindingExpression.WeakPropertyChangedProxy Listener { get; set; }
		}

		void Subscribe(TSource sourceObject)
		{
			if (_handlers == null)
				return;

			for (var i = 0; i < _handlers.Length;i++) {
				var partGetter = _handlers[i].PartGetter;
				var propertyName = _handlers[i].PropertyName;
				var part = partGetter(sourceObject);
				if (part == null) {
					_handlers [i].Part = null;
					break;
				}
				if (!(part is INotifyPropertyChanged))
					continue;
				_handlers [i].Part = new WeakReference(part);
				PropertyChangedEventHandler listener = (sender, e) => {
					if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != propertyName)
						return;
					Device.BeginInvokeOnMainThread(() => Apply());
				};
				_handlers [i].Listener = new BindingExpression.WeakPropertyChangedProxy((INotifyPropertyChanged)part,listener);
			}
		}

		void Unsubscribe()
		{
			if (_handlers == null)
				return;
			for (var i = 0; i < _handlers.Length; i++)
				(_handlers [i].Listener)?.Unsubscribe();
		}
	}
}