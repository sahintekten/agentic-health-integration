"""validate.run once had a parameter called `repair`, which hid the repair() function
and crashed the first time a repair was needed. Parameter names must not hide module functions."""

import inspect

from agents import validate


def test_run_parameters_do_not_hide_module_functions():
    for name in inspect.signature(validate.run).parameters:
        assert not callable(getattr(validate, name, None)), f"parameter {name!r} hides validate.{name}()"
