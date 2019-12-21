"Exposes some of the Harmony 1.2 interface to Python"

__all__ = ['Harmony','usingrefs']

import clr
call = lambda x: x()
#Call the setup function immediately. There's no need for lazy loading here
#since this won't run until someone imports the module anyway.
@call
def _setup():
    lazy_dependencies = [
        [0, "MonoMod.Utils, Version=19.7.4.3, Culture=neutral, PublicKeyToken=null"],
        [0, "Mono.Cecil, Version=0.10.4.0, Culture=neutral, PublicKeyToken=50cebf1cceb9d05e"],
        [1, "IronPython, Version=2.7.7.0, Culture=neutral, PublicKeyToken=7f709c5b713576e1"],
        [1, "0Harmony, Version=1.2.0.1, Culture=neutral, PublicKeyToken=null"],
        ]
    for state, qname in lazy_dependencies:
        if state <= 0:
            clr.LoadAssemblyByName(qname)
        if state <= 1:
            clr.AddReferenceByName(qname)

import sys, MonoMod, IronPython, System, Python
from Harmony import HarmonyInstance, HarmonyMethod
from MonoMod.Utils import DynamicMethodDefinition



class Harmony(object):
    def __init__(self, _id):
        self.instance = HarmonyInstance.Create(_id)

    @staticmethod
    def _get_target_info(f):
        if type(f) == clr.GetPythonType(IronPython.Runtime.Types.BuiltinMethodDescriptor):
            f = f._BuiltinMethodDescriptor___template
        if type(f) == clr.GetPythonType(IronPython.Runtime.Types.BuiltinFunction):
            if len(f.Targets) != 1:
                raise ValueError("Ambiguous function specification. You may " + \
                                 "be missing type parameters for a generic " + \
                                 "or overloaded function.")
            r = { 'target' : f.Targets[0],
                  'name' : f._BuiltinFunction__Name,
                  'declaring_type' : f.DeclaringType, }
            return r
        raise TypeError("Incompatible type " + repr(type(f)) +
                        ". Please see the documentation for allowable types")

    @staticmethod
    def _make_harmony_method(method, target_info):
        s = lambda: None #specification container
        s.core_func = method.__func__
        s.patch_kind = method.__func__.__name__ # will be (prefix/postfix/transpiler)
        s.patch_name = target_info['declaring_type'].FullName + '_' + \
                       target_info['name'] + '_' + s.patch_kind

        if s.patch_kind in ('prefix','postfix'):
            s.patch_return_type = {
                'prefix': clr.GetClrType(bool),
                'postfix': None,
                }[s.patch_kind]
            s.refs = getattr(s.core_func, 'refs', set()) #method.__func__.refs are which params can be written to
            _c = s.core_func.__code__
            s.pymethod_param_list = list(_c.co_varnames[:_c.co_argcount])
            _pg = [(p.Name, p.ParameterType) for p in target_info['target'].GetParameters()]
            target_param_info = dict(_pg)
            target_param_list = zip(*_pg)[0]
            _valid_refs = set(target_param_list) | {'__result'}
            _valid_params = _valid_refs | {'__instance'}
            if not s.refs <= _valid_refs:
                raise ValueError("Specified refs don't correspond to valid, referencable names")
            if not set(s.pymethod_param_list) <= _valid_params:
                raise ValueError("Patch parameters and target method parameters are incompatible")
            s.patch_param_names = s.pymethod_param_list + list(s.refs - set(s.pymethod_param_list))
            s.patch_param_types = []
            for n in s.patch_param_names:
                #if n == '__result':
                #    t = target_info['target'].ReturnType
                #elif n == '__instance':
                #    t = target_info['declaring_type']
                #else:
                #    t = target_param_info[n]
                # !!! actually, just make everything into an object instead.
                #     _emit_affix_body will thank us later.
                t = System.Object
                if n in s.refs:
                    t = t.MakeByRefType()
                s.patch_param_types.append(t)
            wrap_addr = Python.AddressedStorage.Store(Harmony._get_affix_wrapper(s))
            dmd = DynamicMethodDefinition(
                s.patch_name, s.patch_return_type, System.Array[System.Type](s.patch_param_types))
            for i,n in zip(range(len(s.patch_param_names)),s.patch_param_names):
                dmd.Definition.Parameters[i].Name = n
            Harmony._emit_affix_body(dmd.GetILGenerator(), s, wrap_addr)
        elif s.patch_kind == 'transpiler':
            T = clr.GetClrType(System.Collections.IEnumerable[
                Harmony.CodeInstruction])
            raise NotImplementedError("Transpilers not supported yet")

        dmd.Generator = DynamicMethodDefinition.GeneratorType.Cecil
        return HarmonyMethod(dmd.Generate())

    @staticmethod
    def _get_affix_wrapper(specification):
        #result is stored in the closure so won't need to be always recreated
        result = System.Array[System.Object]((None,None))
        def wrapper(*args):
            r = specification.core_func(*args)
            ret, ass = None, {}
            t = type(r)
            if type(r) is tuple:
                ret, ass = r
            elif type(r) is dict:
                ass = r
            else:
                ret = r
            result[0] = ret
            result[1] = ass
            return result
        return wrapper
            
    @staticmethod
    def _emit_affix_body(il, s, inner_addr): #s: specification
        #helpers
        c = System.Reflection.Emit.OpCodes
        Ldc_I4_ = lambda x: (getattr(c, "Ldc_I4_"+str(x)),) \
                            if (x in range(9)) else (c.Ldc_I4, x)
        Ldarg_ = lambda x: (getattr(c,"Ldarg_"+str(param_i)),) \
                           if (param_i in range(4)) else (c.Ldarg_S, param_i)
        methodinfo = lambda f: Harmony._get_target_info(f)['target']
            
        #0  python function return values
        il.DeclareLocal(System.Array[System.Object])
        #1  IDictionary assignments
        assdict_type = System.Collections.Generic.IDictionary[System.String, System.Object]
        il.DeclareLocal(assdict_type)
        #2  out object for TryGetValue
        il.DeclareLocal(System.Object)

        il.Emit(c.Nop)

        #get python function
        il.Emit(c.Ldc_I8, System.Int64(inner_addr))
        il.Emit(c.Call, methodinfo(Python.AddressedStorage.Fetch))
        #stack: (python function)

        #set up args
        il.Emit(*Ldc_I4_(len(s.pymethod_param_list)))
        il.Emit(c.Newarr, System.Object)
        #stack: (python function , arg array)
        _g = zip(range(len(s.pymethod_param_list)), s.pymethod_param_list)
        for array_i, pyparam in _g:
            #put array copy
            il.Emit(c.Dup)
            #put array index
            il.Emit(*Ldc_I4_(array_i))
            #put value from arg
            param_i = s.patch_param_names.index(pyparam)
            il.Emit(*Ldarg_(param_i))
            if pyparam in s.refs:
                il.Emit(c.Ldind_Ref)
            #save to array
            il.Emit(c.Stelem_Ref)
        #stack: (python function , arg array)

        #call main (core) function
        Call_targets = IronPython.Runtime.Operations.PythonCalls.Call.Overloads[
            System.Object, System.Array[System.Object]
            ].Targets
        Call = dict((len(t.GetParameters()),t) for t in Call_targets)[2]
        il.Emit(c.Call, Call)
        il.Emit(c.Castclass, System.Array[System.Object])
        il.Emit(c.Stloc_0)
        #stack: ()

        if s.refs:
            #try load assignments dictionary
            il.Emit(c.Ldloc_0)
            il.Emit(c.Ldc_I4_1)
            il.Emit(c.Ldelem_Ref)
            il.Emit(c.Isinst, assdict_type)
            il.Emit(c.Stloc_1)
            #stack: ()

            il.Emit(c.Nop)

            #jump to end if assignments dict is null
            il.Emit(c.Ldloc_1)
            label_assdict_null = il.DefineLabel()
            il.Emit(c.Brfalse_S, label_assdict_null)
            #stack: ()

            for ref in s.refs:
                #trygetvalue
                il.Emit(c.Nop)
                il.Emit(c.Ldloc_1)
                il.Emit(c.Ldstr, ref)
                il.Emit(c.Ldloca_S, 2)
                il.Emit(c.Callvirt, methodinfo(assdict_type.TryGetValue))
                label_skip_saveref = il.DefineLabel()
                #stack: ()
                #skip if key was not found
                il.Emit(c.Brfalse_S, label_skip_saveref)

                #set reference
                param_i = s.patch_param_names.index(ref)
                il.Emit(*Ldarg_(param_i))
                il.Emit(c.Ldloc_2)
                il.Emit(c.Stind_Ref)

                il.MarkLabel(label_skip_saveref)

            il.MarkLabel(label_assdict_null)

        #return
        il.Emit(c.Nop)
        if s.patch_return_type is not None:
            il.Emit(c.Ldloc_0)
            il.Emit(c.Ldc_I4_0)
            il.Emit(c.Ldelem_Ref)
            if System.Type.IsAssignableFrom(System.ValueType, s.patch_return_type):
                il.Emit(c.Unbox_Any, s.patch_return_type)
            else:
                il.Emit(c.Isinst, s.patch_return_type)
        il.Emit(c.Ret)

    def patch(self, cls):
        #getting target as a basic attribute of cls is fine.
        #if users want dynamic resolution they can set up a __get__
        target_info = self._get_target_info(cls.target)
        args = {'original': target_info['target']}
        for name in ('prefix','postfix','transpiler'):
            method = getattr(cls, name, None)
            if method:
                args[name] = self._make_harmony_method(method, target_info)
        cls.patch = self.instance.Patch(**args)
        return cls

#def get_overload(f, types):
#    types = [clr.GetClrType(t) for t in types]
#    overloads = dict((list(k._TypeList___types),v) for k,v in f._BuiltinFunction__OverloadDictionary)
#    return overloads[types]

def usingrefs(*args):
    def dec(f):
        v = set(args)
        if len(args) is not len(v):
            raise ValueError("Duplicate ref names.")
        f.refs = v
        return f
    return dec
